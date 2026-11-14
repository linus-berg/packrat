using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using PackRat.Configuration;
using PackRat.Hosting;
using PackRat.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PackRat.Cli;

public static class PackRatCli {
  public const int C_EXIT_OK = 0;
  public const int C_EXIT_FAILURE = 1;

  /// <summary>EX_CONFIG; the systemd unit doesn't restart on it.</summary>
  public const int C_EXIT_CONFIG = 78;

  public static string Version { get; } = ReadVersion();

  public static Task<int> RunAsync(string[] args) {
    Option<string?> config = new("--config", "-c") {
      Description = "Config file. Default: $" +
                    ConfigLoader.C_CONFIG_ENVIRONMENT_VARIABLE + ", then " +
                    ConfigLoader.C_DEFAULT_CONFIG_PATH + ".",
      HelpName = "path"
    };
    Option<bool> dry_run = new("--dry-run", "-n") {
      Description = "Log what would be uploaded and deleted, change nothing."
    };
    Option<bool> verbose = new("--verbose", "-v") {
      Description = "Debug logging."
    };

    // A plain Command so help shows "packrat", not the DLL name.
    Command root = new(
      ConfigLoader.C_APP_NAME,
      "PackRat watches directories and moves their files into S3, " +
      "keeping relative paths. It hoards everything you give it, just not " +
      "on your disk. Runs until stopped; normally started by systemd as " +
      "packrat@<user>.service."
    );
    root.Options.Add(new HelpOption());
    root.Options.Add(new VersionOption());
    root.Options.Add(config);
    root.Options.Add(dry_run);
    root.Options.Add(verbose);
    root.SetAction((parse_result, _) => RunServiceAsync(
                     parse_result.GetValue(config),
                     parse_result.GetValue(dry_run),
                     parse_result.GetValue(verbose)
                   )
    );

    // The host handles SIGINT/SIGTERM itself (and lets in-flight uploads
    // cancel cleanly), so System.CommandLine must not.
    InvocationConfiguration invocation = new() {
      ProcessTerminationTimeout = null
    };
    return root.Parse(args).InvokeAsync(invocation);
  }

  private static async Task<int> RunServiceAsync(string? config_path,
                                                 bool dry_run,
                                                 bool verbose
  ) {
    ResolvedConfig config;
    try {
      config = ConfigLoader.Load(
        config_path,
        dry_run,
        Environment.GetEnvironmentVariable
      );
    } catch (ConfigurationException e) {
      await Console.Error.WriteLineAsync(e.Message);
      return C_EXIT_CONFIG;
    }

    HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
      new HostApplicationBuilderSettings {
        ApplicationName = ConfigLoader.C_APP_NAME,
        ContentRootPath = AppContext.BaseDirectory
      }
    );
    ServiceCollectionExtensions.ConfigureLogging(builder.Logging, verbose);
    builder.Services.AddSystemd();
    builder.Services.AddPackRat(config);
    builder.Services.AddHostedService<SyncWorker>();

    using IHost host = builder.Build();
    ILogger logger = host.Services
                         .GetRequiredService<ILoggerFactory>()
                         .CreateLogger("PackRat");
    LogStartup(logger, config);

    try {
      await host.RunAsync();
      return C_EXIT_OK;
    } catch (ConfigurationException e) {
      // E.g. an AWS profile or proxy URL that only fails once used.
      logger.LogCritical("{Error}", e.Message);
      return C_EXIT_CONFIG;
    }
  }

  private static void LogStartup(ILogger logger, ResolvedConfig config) {
    logger.LogInformation(
      "PackRat {Version} watching {Count} director{Plural} " +
      "(config: {Config})",
      Version,
      config.Roots.Count,
      config.Roots.Count == 1 ? "y" : "ies",
      config.ConfigPath
    );
    foreach (SyncRoot root in config.Roots) {
      logger.LogInformation(
        "  {Path} -> {Destination} ({Mode})",
        root.Path,
        root.Destination,
        root.DeleteAfterUpload ? "move" : "copy, keep local files"
      );
    }

    foreach (string warning in config.Warnings) {
      logger.LogWarning("{Warning}", warning);
    }

    if (config.Sync.DryRun) {
      logger.LogWarning("Dry run: nothing will be uploaded or deleted");
    }
  }

  private static string ReadVersion() {
    Assembly assembly = typeof(PackRatCli).Assembly;
    string? version = assembly
                      .GetCustomAttribute<
                        AssemblyInformationalVersionAttribute>()
                      ?.InformationalVersion;
    if (string.IsNullOrEmpty(version)) {
      return "0.0.0";
    }

    int plus = version.IndexOf('+');
    return plus < 0 ? version : version[..plus];
  }
}
