namespace PackRat.Sync;

/// <summary>A local directory and where its contents go.</summary>
public sealed record SyncRoot(string Path,
                              string Bucket,
                              string Prefix,
                              bool DeleteAfterUpload,
                              string? StorageClass,
                              ExcludeFilter Excludes
) {
  public string Destination =>
    $"s3://{Bucket}/{ObjectKeys.ListingPrefix(Prefix)}";
}

/// <summary>Engine-wide settings.</summary>
public sealed record SyncSettings(TimeSpan ScanInterval,
                                  TimeSpan SettleTime,
                                  int MaxConcurrentUploads,
                                  bool VerifyUploads,
                                  bool DeleteEmptyDirectories,
                                  bool Watch,
                                  bool DryRun
);
