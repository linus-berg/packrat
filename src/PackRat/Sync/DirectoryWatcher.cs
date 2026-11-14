using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PackRat.Sync;

/// <summary>
///   inotify-backed nudges so new files are noticed before the next periodic
///   scan. Purely an optimisation: if a watch can't be created (missing
///   directory, inotify limits) the periodic scan still finds everything.
/// </summary>
public sealed class DirectoryWatcher : IDisposable {
  private static readonly TimeSpan S_DEBOUNCE_ = TimeSpan.FromSeconds(2);

  private readonly IReadOnlyList<SyncRoot> roots_;
  private readonly ILogger logger_;
  private readonly Dictionary<string, FileSystemWatcher> watchers_ = [];
  private readonly HashSet<string> failed_ = [];
  private readonly Lock lock_ = new();
  private readonly Channel<bool> changes_ = Channel.CreateBounded<bool>(
    new BoundedChannelOptions(1) {
      FullMode = BoundedChannelFullMode.DropWrite,
      SingleReader = true
    }
  );

  public DirectoryWatcher(IReadOnlyList<SyncRoot> roots, ILogger logger) {
    roots_ = roots;
    logger_ = logger;
  }

  /// <summary>
  ///   (Re)creates watches for roots that exist but aren't watched.
  /// </summary>
  public void EnsureWatching() {
    lock (lock_) {
      foreach (SyncRoot root in roots_) {
        if (watchers_.ContainsKey(root.Path) || !Directory.Exists(root.Path)) {
          continue;
        }

        FileSystemWatcher? watcher = null;
        try {
          watcher = new FileSystemWatcher(root.Path) {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size
          };
          string path = root.Path;
          watcher.Created += (_, _) => Signal();
          watcher.Changed += (_, _) => Signal();
          watcher.Renamed += (_, _) => Signal();
          watcher.Error += (_, e) => OnError(path, e.GetException());
          watcher.EnableRaisingEvents = true;
          watchers_[root.Path] = watcher;
          if (failed_.Remove(root.Path)) {
            logger_.LogInformation("Watching {Path} again", root.Path);
          }
        } catch (Exception e) when (e is IOException
                                      or ArgumentException
                                      or UnauthorizedAccessException) {
          watcher?.Dispose();
          if (failed_.Add(root.Path)) {
            logger_.LogWarning(
              "Cannot watch {Path} ({Error}); relying on periodic scans. " +
              "Raising fs.inotify.max_user_watches may help",
              root.Path,
              e.Message
            );
          }
        }
      }
    }
  }

  /// <summary>
  ///   Waits until <paramref name="timeout" /> passes or something changes in
  ///   a watched directory. Returns true when woken by a change.
  /// </summary>
  public async Task<bool> WaitAsync(TimeSpan timeout,
                                    TimeProvider time,
                                    CancellationToken cancellation_token
  ) {
    using CancellationTokenSource deadline = new(timeout, time);
    using CancellationTokenSource linked =
      CancellationTokenSource.CreateLinkedTokenSource(
        cancellation_token,
        deadline.Token
      );
    try {
      await changes_.Reader.WaitToReadAsync(linked.Token);
    } catch (OperationCanceledException)
      when (!cancellation_token.IsCancellationRequested) {
      return false;
    }

    // Let a burst of events (a copy in progress, an unzip) settle a bit.
    await Task.Delay(S_DEBOUNCE_, time, cancellation_token);
    while (changes_.Reader.TryRead(out _)) {
      // Drain the burst.
    }

    return true;
  }

  private void Signal() {
    changes_.Writer.TryWrite(true);
  }

  private void OnError(string path, Exception error) {
    logger_.LogWarning(
      "Watch on {Path} failed ({Error}); it will be recreated",
      path,
      error.Message
    );
    lock (lock_) {
      if (watchers_.Remove(path, out FileSystemWatcher? watcher)) {
        watcher.Dispose();
      }
    }

    Signal();
  }

  public void Dispose() {
    lock (lock_) {
      foreach (FileSystemWatcher watcher in watchers_.Values) {
        watcher.Dispose();
      }

      watchers_.Clear();
    }
  }
}
