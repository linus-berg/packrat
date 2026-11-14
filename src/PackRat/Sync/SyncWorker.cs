using PackRat.Configuration;
using PackRat.Networking;
using PackRat.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PackRat.Sync;

/// <summary>The service's main loop.</summary>
public sealed class SyncWorker : BackgroundService {
  private static readonly TimeSpan S_MIN_DELAY_ = TimeSpan.FromSeconds(1);

  private readonly SyncEngine engine_;
  private readonly IObjectStore store_;
  private readonly RoutingProxy proxy_;
  private readonly SyncSettings settings_;
  private readonly TimeProvider time_;
  private readonly ILogger<SyncWorker> logger_;

  public SyncWorker(SyncEngine engine,
                    IObjectStore store,
                    RoutingProxy proxy,
                    ResolvedConfig config,
                    TimeProvider time,
                    ILogger<SyncWorker> logger
  ) {
    engine_ = engine;
    store_ = store;
    proxy_ = proxy;
    settings_ = config.Sync;
    time_ = time;
    logger_ = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stopping_token) {
    await Task.Yield();
    LogProxySettings(proxy_.Settings);
    // Runs alongside the first passes so one unreachable bucket (tunnel
    // still down) doesn't hold up the others.
    Task probes = ProbeBucketsAsync(stopping_token);

    using DirectoryWatcher? watcher = settings_.Watch
      ? new DirectoryWatcher(engine_.Roots, logger_)
      : null;

    while (!stopping_token.IsCancellationRequested) {
      watcher?.EnsureWatching();

      SyncPassResult? result = null;
      try {
        result = await engine_.RunOnceAsync(stopping_token);
      } catch (OperationCanceledException)
        when (stopping_token.IsCancellationRequested) {
        break;
      } catch (Exception e) {
        logger_.LogError(e, "Sync pass failed");
      }

      TimeSpan delay = NextDelay(result);
      try {
        if (watcher is null) {
          await Task.Delay(delay, time_, stopping_token);
        } else if (await watcher.WaitAsync(delay, time_, stopping_token)) {
          logger_.LogDebug("Change detected, scanning");
        }
      } catch (OperationCanceledException)
        when (stopping_token.IsCancellationRequested) {
        break;
      }
    }

    try {
      await probes;
    } catch (OperationCanceledException) {
      // Stopping.
    }

    logger_.LogInformation("Stopped");
  }

  private TimeSpan NextDelay(SyncPassResult? result) {
    TimeSpan delay = settings_.ScanInterval;
    if (result?.NextDueAt is DateTimeOffset due) {
      // Wake up just after the next file settles or a retry is due.
      TimeSpan until_due = due - time_.GetUtcNow() + S_MIN_DELAY_;
      if (until_due < delay) {
        delay = until_due;
      }
    }

    return delay < S_MIN_DELAY_ ? S_MIN_DELAY_ : delay;
  }

  /// <summary>Everything needed to debug proxy / tunnel routing.</summary>
  private void LogProxySettings(ProxySettings settings) {
    if (!settings.Enabled) {
      logger_.LogInformation("Proxy: disabled in config, connecting directly");
      return;
    }

    logger_.LogInformation(
      "Proxy: https {Https}, http {Http}",
      Describe(settings.HttpsProxy),
      Describe(settings.HttpProxy)
    );
    if (settings.NoProxy.Rules.Count > 0) {
      logger_.LogInformation(
        "no_proxy: {Rules}{Resolve}",
        string.Join(", ", settings.NoProxy.Rules),
        settings.NoProxy.HasNetworkRules && settings.ResolveHostnames
          ? " (host names resolving into IP/CIDR entries go direct)"
          : string.Empty
      );
    }

    foreach (string invalid in settings.NoProxy.Invalid) {
      logger_.LogWarning("Ignoring invalid no_proxy entry '{Entry}'", invalid);
    }
  }

  private static string Describe(ProxyEndpoint? endpoint) {
    return endpoint is null
      ? "none"
      : $"{endpoint.Display} (from {endpoint.Source})";
  }

  /// <summary>
  ///   One cheap request per bucket so routing (proxy vs direct) and
  ///   credential problems show up in the journal right away. Failures are
  ///   only logged: the network or a VPN tunnel may simply not be up yet.
  /// </summary>
  private async Task ProbeBucketsAsync(CancellationToken stopping_token) {
    IEnumerable<SyncRoot> one_per_bucket = engine_.Roots
                                                  .GroupBy(r => r.Bucket)
                                                  .Select(g => g.First());
    foreach (SyncRoot root in one_per_bucket) {
      try {
        string result = await store_.ProbeAsync(
          root.Bucket,
          ObjectKeys.ListingPrefix(root.Prefix),
          stopping_token
        );
        logger_.LogInformation(
          "Bucket {Bucket}: {Result}",
          root.Bucket,
          result
        );
      } catch (OperationCanceledException)
        when (stopping_token.IsCancellationRequested) {
        throw;
      } catch (Exception e) {
        logger_.LogWarning(
          "Bucket {Bucket} is not usable yet ({Error}); will keep trying",
          root.Bucket,
          Flatten(e)
        );
      }
    }
  }

  private static string Flatten(Exception error) {
    List<string> messages = [];
    for (Exception? current = error;
         current is not null;
         current = current.InnerException) {
      string message = current.Message.TrimEnd('.');
      // Skip the SDK's placeholder inner exceptions.
      if (!message.StartsWith("Exception of type", StringComparison.Ordinal) &&
          !messages.Contains(message)) {
        messages.Add(message);
      }
    }

    return string.Join(" -> ", messages);
  }
}
