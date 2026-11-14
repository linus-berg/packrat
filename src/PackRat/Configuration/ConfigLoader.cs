using PackRat.Networking;
using PackRat.Sync;
using Microsoft.Extensions.Configuration;

namespace PackRat.Configuration;

public static class ConfigLoader {
  public const string C_APP_NAME = "packrat";
  public const string C_CONFIG_ENVIRONMENT_VARIABLE = "PACKRAT_CONFIG";
  public const string C_DEFAULT_CONFIG_PATH =
    "/etc/packrat/config.json";

  /// <summary>
  ///   --config, then $PACKRAT_CONFIG, then
  ///   /etc/packrat/config.json.
  /// </summary>
  public static string FindConfigFile(string? explicit_path,
                                      Func<string, string?> environment
  ) {
    string? requested = explicit_path ??
                        environment(C_CONFIG_ENVIRONMENT_VARIABLE);
    string path = string.IsNullOrWhiteSpace(requested)
      ? C_DEFAULT_CONFIG_PATH
      : Path.GetFullPath(ExpandHome(requested, HomeDirectory(environment)));
    if (!File.Exists(path)) {
      throw new ConfigurationException(
        $"Config file '{path}' does not exist."
      );
    }

    return path;
  }

  public static PackRatOptions Read(string path) {
    PackRatOptions options = new();
    try {
      IConfigurationRoot configuration = new ConfigurationBuilder()
                                         .AddJsonFile(
                                           path,
                                           false,
                                           false
                                         )
                                         .Build();
      configuration.Bind(
        options,
        binder => binder.ErrorOnUnknownConfiguration = true
      );
    } catch (Exception e) when (e is InvalidDataException
                                  or FormatException
                                  or InvalidOperationException) {
      throw new ConfigurationException(
        $"Could not read '{path}': {e.Message}",
        e
      );
    }

    return options;
  }

  public static ResolvedConfig Load(string? explicit_path,
                                    bool dry_run,
                                    Func<string, string?> environment
  ) {
    string path = FindConfigFile(explicit_path, environment);
    return Resolve(Read(path), path, dry_run, HomeDirectory(environment));
  }

  public static ResolvedConfig Resolve(PackRatOptions options,
                                       string config_path,
                                       bool dry_run,
                                       string home
  ) {
    List<string> errors = [];
    List<string> warnings = [];

    ResolvedAws aws = ResolveAws(options.Aws, errors);
    SyncSettings sync = ResolveSync(options.Sync, dry_run, errors);
    List<SyncRoot> roots = ResolveRoots(options, home, errors, warnings);

    if (!string.IsNullOrWhiteSpace(options.Proxy.Url)) {
      try {
        ProxySettings.ParseProxy(options.Proxy.Url, "config: proxy.url");
      } catch (ConfigurationException e) {
        errors.Add(e.Message);
      }
    }

    if (errors.Count > 0) {
      throw new ConfigurationException(
        $"Invalid {config_path}:" + Environment.NewLine +
        string.Join(Environment.NewLine, errors.Select(e => "  - " + e))
      );
    }

    return new ResolvedConfig(
      config_path,
      aws,
      options.Proxy,
      sync,
      roots,
      warnings
    );
  }

  public static string ExpandHome(string path, string home) {
    if (path == "~") {
      return home;
    }

    return path.StartsWith("~/", StringComparison.Ordinal)
      ? Path.Combine(home, path[2..])
      : path;
  }

  private static string HomeDirectory(Func<string, string?> environment) {
    string? home = environment("HOME");
    return string.IsNullOrWhiteSpace(home)
      ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
      : home;
  }

  private static ResolvedAws ResolveAws(AwsOptions aws, List<string> errors) {
    string? service_url = Blank(aws.ServiceUrl);
    if (service_url is not null &&
        (!Uri.TryCreate(service_url, UriKind.Absolute, out Uri? uri) ||
         uri.Scheme is not ("http" or "https"))) {
      errors.Add(
        $"aws.serviceUrl '{service_url}' must be an http(s) URL."
      );
    }

    if (aws.MaxRetries is < 0 or > 20) {
      errors.Add("aws.maxRetries must be between 0 and 20.");
    }

    bool when_required = false;
    string checksums = (aws.Checksums ?? string.Empty).Replace("_", "");
    if (string.Equals(
          checksums,
          "WhenRequired",
          StringComparison.OrdinalIgnoreCase
        )) {
      when_required = true;
    } else if (!string.Equals(
                 checksums,
                 "WhenSupported",
                 StringComparison.OrdinalIgnoreCase
               )) {
      errors.Add("aws.checksums must be 'WhenSupported' or 'WhenRequired'.");
    }

    return new ResolvedAws(
      Blank(aws.Profile),
      Blank(aws.Region),
      service_url,
      aws.ForcePathStyle,
      aws.MaxRetries,
      ParseDuration(aws.ConnectTimeout, "aws.connectTimeout", errors, true),
      ParseDuration(aws.RequestTimeout, "aws.requestTimeout", errors, true),
      when_required
    );
  }

  private static SyncSettings ResolveSync(SyncOptions sync,
                                          bool dry_run,
                                          List<string> errors
  ) {
    TimeSpan scan_interval = ParseDuration(
      sync.ScanInterval,
      "sync.scanInterval",
      errors,
      true
    );
    if (scan_interval > TimeSpan.Zero &&
        scan_interval < TimeSpan.FromSeconds(1)) {
      errors.Add("sync.scanInterval must be at least 1s.");
    }

    if (sync.MaxConcurrentUploads is < 1 or > 64) {
      errors.Add("sync.maxConcurrentUploads must be between 1 and 64.");
    }

    return new SyncSettings(
      scan_interval,
      ParseDuration(sync.SettleTime, "sync.settleTime", errors, false),
      sync.MaxConcurrentUploads,
      sync.VerifyUploads,
      sync.DeleteEmptyDirectories,
      sync.Watch,
      dry_run
    );
  }

  private static List<SyncRoot> ResolveRoots(PackRatOptions options,
                                             string home,
                                             List<string> errors,
                                             List<string> warnings
  ) {
    List<SyncRoot> roots = [];
    if (options.Directories.Count == 0) {
      errors.Add("No directories configured.");
      return roots;
    }

    List<string> shared_excludes = [];
    if (options.Sync.UseDefaultExcludes) {
      shared_excludes.AddRange(ExcludeFilter.S_DEFAULT_PATTERNS);
    }

    shared_excludes.AddRange(options.Sync.Exclude);

    for (int i = 0; i < options.Directories.Count; i++) {
      DirectoryOptions directory = options.Directories[i];
      string label = $"directories[{i}]";
      string? raw_path = Blank(directory.Path);
      if (raw_path is null) {
        errors.Add($"{label}.path is required.");
        continue;
      }

      string expanded = ExpandHome(raw_path, home);
      if (!Path.IsPathRooted(expanded)) {
        errors.Add(
          $"{label}.path '{raw_path}' must be absolute or start with '~/'."
        );
        continue;
      }

      string path = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(expanded)
      );
      if (path == Path.GetPathRoot(path)) {
        errors.Add($"{label}.path must not be the file system root.");
        continue;
      }

      string? bucket = Blank(directory.Bucket) ?? Blank(options.Bucket);
      if (bucket is null) {
        errors.Add(
          $"{label} ({path}) has no bucket; set 'bucket' on the directory " +
          "or at the top level."
        );
        continue;
      }

      if (bucket.Any(c => c == '/' || char.IsWhiteSpace(c))) {
        errors.Add($"{label}.bucket '{bucket}' is not a valid bucket name.");
        continue;
      }

      roots.Add(
        new SyncRoot(
          path,
          bucket,
          ObjectKeys.NormalizePrefix(directory.Prefix),
          directory.DeleteAfterUpload ?? options.Sync.DeleteAfterUpload,
          Blank(directory.StorageClass) ?? Blank(options.Sync.StorageClass),
          new ExcludeFilter(shared_excludes.Concat(directory.Exclude))
        )
      );
    }

    CheckOverlaps(roots, errors, warnings);
    return roots;
  }

  private static void CheckOverlaps(List<SyncRoot> roots,
                                    List<string> errors,
                                    List<string> warnings
  ) {
    for (int i = 0; i < roots.Count; i++) {
      for (int j = i + 1; j < roots.Count; j++) {
        SyncRoot a = roots[i];
        SyncRoot b = roots[j];
        if (a.Path == b.Path) {
          errors.Add($"Directory '{a.Path}' is configured more than once.");
        } else if (IsInside(a.Path, b.Path) || IsInside(b.Path, a.Path)) {
          errors.Add(
            $"Directories '{a.Path}' and '{b.Path}' overlap; one is inside " +
            "the other."
          );
        } else if (a.Bucket == b.Bucket && a.Prefix == b.Prefix) {
          warnings.Add(
            $"'{a.Path}' and '{b.Path}' both upload to {a.Destination}; " +
            "files with the same relative path will overwrite each other."
          );
        }
      }
    }
  }

  private static bool IsInside(string parent, string child) {
    return child.StartsWith(parent + "/", StringComparison.Ordinal);
  }

  private static TimeSpan ParseDuration(string? text,
                                        string name,
                                        List<string> errors,
                                        bool must_be_positive
  ) {
    if (!Durations.TryParse(text, out TimeSpan duration)) {
      errors.Add(
        $"{name} '{text}' is not a duration; use e.g. '30s', '5m' or '1h'."
      );
      return TimeSpan.Zero;
    }

    if (must_be_positive && duration <= TimeSpan.Zero) {
      errors.Add($"{name} must be greater than zero.");
    }

    return duration;
  }

  private static string? Blank(string? value) {
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  }
}
