using Amazon.S3;
using PackRat.Configuration;
using PackRat.Networking;
using PackRat.Storage;
using PackRat.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;

namespace PackRat.Hosting;

public static class ServiceCollectionExtensions {
  public static IServiceCollection AddPackRat(
    this IServiceCollection services,
    ResolvedConfig config
  ) {
    services.AddSingleton(config);
    services.TryAddSingleton(TimeProvider.System);
    services.AddSingleton(
      provider => CreateProxy(
        config.Proxy,
        provider.GetRequiredService<ILogger<RoutingProxy>>(),
        provider.GetRequiredService<TimeProvider>()
      )
    );
    services.AddSingleton<IAmazonS3>(
      provider => S3ClientFactory.Create(
        config.Aws,
        provider.GetRequiredService<RoutingProxy>()
      )
    );
    services.AddSingleton<IObjectStore, S3ObjectStore>();
    services.AddSingleton(
      provider => new SyncEngine(
        config.Roots,
        config.Sync,
        provider.GetRequiredService<IObjectStore>(),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<ILogger<SyncEngine>>()
      )
    );
    return services;
  }

  /// <summary>
  ///   Creates the proxy and also installs it as
  ///   <see cref="HttpClient.DefaultProxy" />, so requests the AWS SDK makes
  ///   outside the S3 client (SSO, STS assume-role) are routed the same way.
  /// </summary>
  public static RoutingProxy CreateProxy(ProxyOptions options,
                                         ILogger<RoutingProxy> logger,
                                         TimeProvider time
  ) {
    ProxySettings settings = ProxySettings.Resolve(
      options,
      Environment.GetEnvironmentVariable
    );
    RoutingProxy proxy = new(settings, logger, time);
    HttpClient.DefaultProxy = proxy;
    return proxy;
  }

  public static void ConfigureLogging(ILoggingBuilder logging, bool verbose) {
    logging.ClearProviders();
    if (SystemdHelpers.IsSystemdService()) {
      // Prefixes lines with <N> so journald records the right priority.
      logging.AddSystemdConsole(options => {
        options.IncludeScopes = false;
      });
    } else {
      logging.AddSimpleConsole(options => {
        options.SingleLine = true;
        options.IncludeScopes = false;
        options.TimestampFormat = "HH:mm:ss ";
      });
    }

    logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    logging.AddFilter("Microsoft", LogLevel.Warning);
    logging.AddFilter("System", LogLevel.Warning);
  }
}
