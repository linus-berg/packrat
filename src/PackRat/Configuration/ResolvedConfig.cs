using PackRat.Sync;

namespace PackRat.Configuration;

public sealed record ResolvedAws(string? Profile,
                                 string? Region,
                                 string? ServiceUrl,
                                 bool ForcePathStyle,
                                 int MaxRetries,
                                 TimeSpan ConnectTimeout,
                                 TimeSpan RequestTimeout,
                                 bool ChecksumsWhenRequired
);

/// <summary>Validated configuration, ready to run.</summary>
public sealed record ResolvedConfig(string ConfigPath,
                                    ResolvedAws Aws,
                                    ProxyOptions Proxy,
                                    SyncSettings Sync,
                                    IReadOnlyList<SyncRoot> Roots,
                                    IReadOnlyList<string> Warnings
);
