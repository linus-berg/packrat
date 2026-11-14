namespace PackRat.Configuration;

/// <summary>Shape of config.json (keys are case-insensitive).</summary>
public sealed class PackRatOptions {
  /// <summary>Default bucket for directories that don't set one.</summary>
  public string? Bucket { get; set; }

  public AwsOptions Aws { get; set; } = new();

  public ProxyOptions Proxy { get; set; } = new();

  public SyncOptions Sync { get; set; } = new();

  public List<DirectoryOptions> Directories { get; set; } = [];
}

public sealed class AwsOptions {
  /// <summary>Named profile from ~/.aws/config or ~/.aws/credentials.</summary>
  public string? Profile { get; set; }

  public string? Region { get; set; }

  /// <summary>Endpoint for S3-compatible storage (MinIO, Ceph, ...).</summary>
  public string? ServiceUrl { get; set; }

  public bool ForcePathStyle { get; set; }

  public int MaxRetries { get; set; } = 5;

  public string ConnectTimeout { get; set; } = "30s";

  public string RequestTimeout { get; set; } = "15m";

  /// <summary>
  ///   <c>WhenSupported</c> (default) or <c>WhenRequired</c>. Use the latter
  ///   for S3-compatible servers that reject the CRC checksum headers.
  /// </summary>
  public string Checksums { get; set; } = "WhenSupported";
}

public sealed class ProxyOptions {
  /// <summary>When false, every request goes direct.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Proxy for all requests; overrides the environment.</summary>
  public string? Url { get; set; }

  /// <summary>Read http_proxy/https_proxy/all_proxy/no_proxy.</summary>
  public bool UseEnvironment { get; set; } = true;

  /// <summary>Extra no_proxy entries, merged with the environment.</summary>
  public List<string> NoProxy { get; set; } = [];

  /// <summary>
  ///   Resolve destination host names and compare the addresses with IP and
  ///   CIDR no_proxy entries. Lets a host name that resolves into a subnet
  ///   routed through an IPsec tunnel bypass the proxy.
  /// </summary>
  public bool ResolveHostnames { get; set; } = true;
}

public sealed class SyncOptions {
  public string ScanInterval { get; set; } = "1m";

  /// <summary>How long a file must be left untouched before upload.</summary>
  public string SettleTime { get; set; } = "30s";

  public int MaxConcurrentUploads { get; set; } = 4;

  public bool DeleteAfterUpload { get; set; } = true;

  public bool DeleteEmptyDirectories { get; set; } = true;

  /// <summary>HEAD the object after upload before deleting the file.</summary>
  public bool VerifyUploads { get; set; } = true;

  /// <summary>Use inotify to notice new files before the next scan.</summary>
  public bool Watch { get; set; } = true;

  public bool UseDefaultExcludes { get; set; } = true;

  public List<string> Exclude { get; set; } = [];

  public string? StorageClass { get; set; }
}

public sealed class DirectoryOptions {
  public string? Path { get; set; }

  public string? Bucket { get; set; }

  public string? Prefix { get; set; }

  public bool? DeleteAfterUpload { get; set; }

  public string? StorageClass { get; set; }

  public List<string> Exclude { get; set; } = [];
}
