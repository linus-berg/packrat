using System.Collections.Concurrent;
using PackRat.Configuration;
using PackRat.Storage;
using Microsoft.Extensions.Logging;

namespace PackRat.Sync;

public sealed record SyncPassResult(int Uploaded,
                                    long BytesUploaded,
                                    int Deleted,
                                    int Failed,
                                    int Waiting,
                                    DateTimeOffset? NextDueAt
);

/// <summary>
///   One pass: scan every root, upload settled files, verify, delete the
///   local copy, tidy up empty directories. Not re-entrant; call
///   <see cref="RunOnceAsync" /> from one loop.
/// </summary>
public sealed class SyncEngine {
  private static readonly TimeSpan S_MIN_RETRY_DELAY_ =
    TimeSpan.FromSeconds(30);

  private static readonly TimeSpan S_MAX_RETRY_DELAY_ = TimeSpan.FromHours(1);

  private readonly IReadOnlyList<SyncRoot> roots_;
  private readonly SyncSettings settings_;
  private readonly IObjectStore store_;
  private readonly TimeProvider time_;
  private readonly ILogger<SyncEngine> logger_;

  // Local path → stamp of the copy known to be in the bucket.
  private readonly ConcurrentDictionary<string, FileStamp> uploaded_ = new();
  private readonly ConcurrentDictionary<string, FailureState> failures_ =
    new();

  private readonly HashSet<string> missing_roots_ = [];
  private readonly HashSet<SyncRoot> listed_roots_ = [];
  private Dictionary<string, Observation> observed_ = [];

  public SyncEngine(IReadOnlyList<SyncRoot> roots,
                    SyncSettings settings,
                    IObjectStore store,
                    TimeProvider time,
                    ILogger<SyncEngine> logger
  ) {
    roots_ = roots;
    settings_ = settings;
    store_ = store;
    time_ = time;
    logger_ = logger;
  }

  public IReadOnlyList<SyncRoot> Roots => roots_;

  public async Task<SyncPassResult> RunOnceAsync(
    CancellationToken cancellation_token
  ) {
    DateTimeOffset now = time_.GetUtcNow();
    PassState pass = new();
    List<WorkItem> work = [];
    List<(SyncRoot Root, LocalEntry Directory)> directories = [];
    Dictionary<string, Observation> observed = [];

    foreach (SyncRoot root in roots_) {
      cancellation_token.ThrowIfCancellationRequested();
      if (!RootExists(root)) {
        continue;
      }

      IReadOnlyDictionary<string, RemoteObject>? remote =
        await ListRemoteOnceAsync(root, cancellation_token);
      Collect(root, now, remote, work, directories, observed, pass);
    }

    observed_ = observed;
    work = SkipFilesOpenForWriting(work, now, pass);

    if (work.Count > 0) {
      await Parallel.ForEachAsync(
        work,
        new ParallelOptions {
          MaxDegreeOfParallelism = settings_.MaxConcurrentUploads,
          CancellationToken = cancellation_token
        },
        async (item, token) => await ProcessAsync(item, pass, token)
      );
    }

    if (settings_.DeleteEmptyDirectories) {
      RemoveEmptyDirectories(directories, now, pass);
    }

    SyncPassResult result = pass.ToResult();
    LogSummary(result);
    return result;
  }

  private List<WorkItem> SkipFilesOpenForWriting(List<WorkItem> work,
                                                 DateTimeOffset now,
                                                 PassState pass
  ) {
    if (work.Count == 0) {
      return work;
    }

    HashSet<string> busy = OpenFiles.OpenForWriting(
      work.Select(w => w.Entry.FullPath)
    );
    if (busy.Count == 0) {
      return work;
    }

    foreach (string path in busy) {
      logger_.LogDebug(
        "Waiting for {Path}: another process has it open for writing",
        path
      );
      pass.AddWaiting(now + settings_.SettleTime);
    }

    return work.Where(w => !busy.Contains(w.Entry.FullPath)).ToList();
  }

  private bool RootExists(SyncRoot root) {
    bool exists = Directory.Exists(root.Path);
    if (!exists && missing_roots_.Add(root.Path)) {
      logger_.LogWarning(
        "Directory {Path} does not exist; skipping it until it does",
        root.Path
      );
    } else if (exists && missing_roots_.Remove(root.Path)) {
      logger_.LogInformation("Directory {Path} is back", root.Path);
    }

    return exists;
  }

  /// <summary>
  ///   Roots that keep their local files need to know what is already in the
  ///   bucket after a restart. Listed once, even if that fails (no
  ///   s3:ListBucket): from then on the engine remembers what it uploaded.
  /// </summary>
  private async Task<IReadOnlyDictionary<string, RemoteObject>?>
    ListRemoteOnceAsync(SyncRoot root, CancellationToken cancellation_token) {
    if (root.DeleteAfterUpload || !listed_roots_.Add(root)) {
      return null;
    }

    try {
      IReadOnlyDictionary<string, RemoteObject> remote = await store_.ListAsync(
        root.Bucket,
        ObjectKeys.ListingPrefix(root.Prefix),
        cancellation_token
      );
      logger_.LogDebug(
        "Found {Count} existing objects in {Destination}",
        remote.Count,
        root.Destination
      );
      return remote;
    } catch (Exception e) when (!cancellation_token.IsCancellationRequested) {
      logger_.LogWarning(
        "Could not list {Destination} ({Error}); files already in the " +
        "bucket are uploaded once more. Listing needs s3:ListBucket",
        root.Destination,
        e.Message
      );
      return null;
    }
  }

  private void Collect(SyncRoot root,
                       DateTimeOffset now,
                       IReadOnlyDictionary<string, RemoteObject>? remote,
                       List<WorkItem> work,
                       List<(SyncRoot Root, LocalEntry Directory)> directories,
                       Dictionary<string, Observation> observed,
                       PassState pass
  ) {
    try {
      foreach (LocalEntry entry in DirectoryScanner.Enumerate(root)) {
        if (entry.IsDirectory) {
          directories.Add((root, entry));
          continue;
        }

        if (root.Excludes.IsExcluded(entry.RelativePath)) {
          continue;
        }

        DateTimeOffset settles_at =
          SettleReference(entry, now, observed) + settings_.SettleTime;
        if (settles_at > now) {
          pass.AddWaiting(settles_at);
          continue;
        }

        if (failures_.TryGetValue(entry.FullPath, out FailureState? failure) &&
            failure.Stamp == entry.Stamp &&
            failure.RetryAt > now) {
          pass.AddDue(failure.RetryAt);
          continue;
        }

        if (!root.DeleteAfterUpload &&
            uploaded_.TryGetValue(entry.FullPath, out FileStamp known) &&
            known == entry.Stamp) {
          continue;
        }

        string key = ObjectKeys.ForRelativePath(
          root.Prefix,
          entry.RelativePath
        );
        if (!ObjectKeys.IsValidLength(key)) {
          RecordFailure(
            entry.FullPath,
            entry.Stamp,
            new PathTooLongException(
              $"S3 keys are limited to {ObjectKeys.C_MAX_KEY_BYTES} bytes"
            ),
            now
          );
          continue;
        }

        if (remote is not null &&
            remote.TryGetValue(key, out RemoteObject? existing) &&
            existing.Size == entry.Stamp.Length &&
            existing.LastModified >= entry.Stamp.LastWriteTimeUtc) {
          uploaded_[entry.FullPath] = entry.Stamp;
          continue;
        }

        work.Add(new WorkItem(root, entry, key));
      }
    } catch (Exception e) when (e is IOException
                                  or UnauthorizedAccessException) {
      logger_.LogWarning(
        "Could not scan {Path}: {Error}",
        root.Path,
        e.Message
      );
    }
  }

  /// <summary>
  ///   Files settle once their mtime is old enough. A file with an mtime in
  ///   the future (clock skew, `touch -d`) settles once it has been seen
  ///   unchanged for the settle time instead.
  /// </summary>
  private DateTimeOffset SettleReference(LocalEntry entry,
                                         DateTimeOffset now,
                                         Dictionary<string, Observation>
                                           observed
  ) {
    if (entry.Stamp.LastWriteTimeUtc <= now) {
      return entry.Stamp.LastWriteTimeUtc;
    }

    DateTimeOffset first_seen =
      observed_.TryGetValue(entry.FullPath, out Observation? previous) &&
      previous.Stamp == entry.Stamp
        ? previous.FirstSeen
        : now;
    observed[entry.FullPath] = new Observation(entry.Stamp, first_seen);
    return first_seen;
  }

  private async Task ProcessAsync(WorkItem item,
                                  PassState pass,
                                  CancellationToken cancellation_token
  ) {
    string path = item.Entry.FullPath;
    FileStamp stamp = item.Entry.Stamp;
    try {
      if (UnixFileSystem.IsRegularFile(path) == false) {
        logger_.LogDebug("Skipping {Path}: not a regular file", path);
        return;
      }

      if (FileStamp.Read(path) != stamp) {
        logger_.LogDebug("Skipping {Path}: changed since the scan", path);
        return;
      }

      bool in_bucket = uploaded_.TryGetValue(path, out FileStamp known) &&
                       known == stamp;
      if (!in_bucket) {
        await UploadAsync(item, cancellation_token);
        uploaded_[path] = stamp;
        pass.AddUploaded(stamp.Length);
      }

      if (!item.Root.DeleteAfterUpload) {
        failures_.TryRemove(path, out _);
        return;
      }

      if (settings_.DryRun) {
        if (!in_bucket) {
          logger_.LogInformation("[dry run] Would delete {Path}", path);
        }

        return;
      }

      DeleteLocalCopy(item, stamp, pass);
    } catch (OperationCanceledException)
      when (cancellation_token.IsCancellationRequested) {
      throw;
    } catch (Exception e) {
      RecordFailure(path, stamp, e, time_.GetUtcNow());
      pass.AddFailed();
    }
  }

  private async Task UploadAsync(WorkItem item,
                                 CancellationToken cancellation_token
  ) {
    string path = item.Entry.FullPath;
    string destination = $"s3://{item.Root.Bucket}/{item.Key}";
    long length = item.Entry.Stamp.Length;
    if (settings_.DryRun) {
      logger_.LogInformation(
        "[dry run] Would upload {Path} to {Destination} ({Size})",
        path,
        destination,
        Sizes.Format(length)
      );
      return;
    }

    await store_.UploadAsync(
      new ObjectUpload(
        item.Root.Bucket,
        item.Key,
        path,
        item.Root.StorageClass,
        item.Entry.Stamp.LastWriteTimeUtc
      ),
      cancellation_token
    );

    if (settings_.VerifyUploads) {
      await VerifyAsync(item, destination, length, cancellation_token);
    }

    logger_.LogInformation(
      "Uploaded {Path} to {Destination} ({Size})",
      path,
      destination,
      Sizes.Format(length)
    );
  }

  private async Task VerifyAsync(WorkItem item,
                                 string destination,
                                 long expected_length,
                                 CancellationToken cancellation_token
  ) {
    long? remote_length;
    try {
      remote_length = await store_.GetObjectSizeAsync(
        item.Root.Bucket,
        item.Key,
        cancellation_token
      );
    } catch (Exception e) when (!cancellation_token.IsCancellationRequested) {
      throw new UploadVerificationException(
        $"uploaded, but could not verify {destination}: {e.Message}. " +
        "Verification needs s3:GetObject; grant it or set " +
        "sync.verifyUploads to false",
        e
      );
    }

    if (remote_length != expected_length) {
      string actual = remote_length?.ToString() ?? "missing";
      throw new UploadVerificationException(
        $"{destination} is {actual} bytes after upload, expected " +
        $"{expected_length}; keeping the local file"
      );
    }
  }

  private void DeleteLocalCopy(WorkItem item,
                               FileStamp uploaded_stamp,
                               PassState pass
  ) {
    string path = item.Entry.FullPath;
    FileStamp? current = FileStamp.Read(path);
    if (current is null) {
      uploaded_.TryRemove(path, out _);
      failures_.TryRemove(path, out _);
      return;
    }

    if (current != uploaded_stamp) {
      uploaded_.TryRemove(path, out _);
      logger_.LogWarning(
        "{Path} changed during the upload; keeping it so the new version " +
        "is uploaded on the next pass",
        path
      );
      return;
    }

    File.Delete(path);
    uploaded_.TryRemove(path, out _);
    failures_.TryRemove(path, out _);
    pass.AddDeleted();
    logger_.LogDebug("Deleted local copy {Path}", path);
  }

  /// <summary>
  ///   Removes subdirectories (never the root) that have been empty and
  ///   untouched for the settle time. A directory's mtime changes whenever
  ///   an entry is added or removed, so a directory emptied in this pass is
  ///   removed on a later one, and one an application just created is left
  ///   alone for a while.
  /// </summary>
  private void RemoveEmptyDirectories(
    List<(SyncRoot Root, LocalEntry Directory)> directories,
    DateTimeOffset now,
    PassState pass
  ) {
    IEnumerable<(SyncRoot Root, LocalEntry Directory)> deepest_first =
      directories.OrderByDescending(d => d.Directory.FullPath.Length);
    foreach ((SyncRoot root, LocalEntry directory) in deepest_first) {
      try {
        if (!DirectoryScanner.IsEmptyDirectory(directory.FullPath)) {
          continue;
        }

        // Re-read: deleting files in this pass touched the mtime.
        DateTimeOffset modified = new(
          Directory.GetLastWriteTimeUtc(directory.FullPath),
          TimeSpan.Zero
        );
        DateTimeOffset removable_at = modified + settings_.SettleTime;
        if (removable_at > now) {
          pass.AddDue(removable_at);
          continue;
        }

        if (settings_.DryRun) {
          logger_.LogInformation(
            "[dry run] Would remove empty directory {Path}",
            directory.FullPath
          );
          continue;
        }

        Directory.Delete(directory.FullPath, false);
        logger_.LogDebug(
          "Removed empty directory {Path} from {Root}",
          directory.FullPath,
          root.Path
        );
      } catch (DirectoryNotFoundException) {
        // Already gone.
      } catch (Exception e) when (e is IOException
                                    or UnauthorizedAccessException) {
        logger_.LogDebug(
          "Could not remove {Path}: {Error}",
          directory.FullPath,
          e.Message
        );
      }
    }
  }

  private void RecordFailure(string path,
                             FileStamp stamp,
                             Exception error,
                             DateTimeOffset now
  ) {
    int attempts = failures_.TryGetValue(path, out FailureState? previous) &&
                   previous.Stamp == stamp
      ? previous.Attempts + 1
      : 1;
    double factor = Math.Pow(2, Math.Min(attempts - 1, 16));
    TimeSpan delay = TimeSpan.FromTicks(
      (long)Math.Min(
        S_MIN_RETRY_DELAY_.Ticks * factor,
        S_MAX_RETRY_DELAY_.Ticks
      )
    );
    failures_[path] = new FailureState(stamp, attempts, now + delay);
    logger_.LogWarning(
      "Failed to upload {Path} (attempt {Attempt}, retrying in {Delay}): " +
      "{Error}",
      path,
      attempts,
      Durations.Format(delay),
      error.Message
    );
    logger_.LogDebug(error, "Failure details for {Path}", path);
  }

  private void LogSummary(SyncPassResult result) {
    if (result.Uploaded == 0 && result.Deleted == 0 && result.Failed == 0) {
      logger_.LogDebug(
        "Nothing to do; {Waiting} file(s) still settling",
        result.Waiting
      );
      return;
    }

    logger_.LogInformation(
      "Pass done: {Uploaded} stashed in the bucket ({Size}), {Deleted} " +
      "local {Copies} cleared out, {Failed} failed, {Waiting} still settling",
      result.Uploaded,
      Sizes.Format(result.BytesUploaded),
      result.Deleted,
      result.Deleted == 1 ? "copy" : "copies",
      result.Failed,
      result.Waiting
    );
  }

  private sealed record WorkItem(SyncRoot Root, LocalEntry Entry, string Key);

  private sealed record FailureState(FileStamp Stamp,
                                     int Attempts,
                                     DateTimeOffset RetryAt
  );

  private sealed record Observation(FileStamp Stamp, DateTimeOffset FirstSeen);

  private sealed class PassState {
    private readonly Lock lock_ = new();
    private int uploaded_;
    private long bytes_;
    private int deleted_;
    private int failed_;
    private int waiting_;
    private DateTimeOffset? next_due_;

    public void AddUploaded(long bytes) {
      Interlocked.Increment(ref uploaded_);
      Interlocked.Add(ref bytes_, bytes);
    }

    public void AddDeleted() {
      Interlocked.Increment(ref deleted_);
    }

    public void AddFailed() {
      Interlocked.Increment(ref failed_);
    }

    public void AddWaiting(DateTimeOffset settles_at) {
      Interlocked.Increment(ref waiting_);
      AddDue(settles_at);
    }

    public void AddDue(DateTimeOffset due) {
      lock (lock_) {
        if (next_due_ is null || due < next_due_) {
          next_due_ = due;
        }
      }
    }

    public SyncPassResult ToResult() {
      lock (lock_) {
        return new SyncPassResult(
          uploaded_,
          bytes_,
          deleted_,
          failed_,
          waiting_,
          next_due_
        );
      }
    }
  }
}

public sealed class UploadVerificationException : Exception {
  public UploadVerificationException(string message) : base(message) { }

  public UploadVerificationException(string message, Exception inner)
    : base(message, inner) { }
}
