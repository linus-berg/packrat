namespace PackRat.Storage;

public sealed record ObjectUpload(string Bucket,
                                  string Key,
                                  string FilePath,
                                  string? StorageClass,
                                  DateTimeOffset LastWriteTimeUtc
);

public sealed record RemoteObject(long Size, DateTimeOffset LastModified);

public interface IObjectStore {
  Task UploadAsync(ObjectUpload upload, CancellationToken cancellation_token);

  /// <summary>Size of the stored object, or null if it doesn't exist.</summary>
  Task<long?> GetObjectSizeAsync(string bucket,
                                 string key,
                                 CancellationToken cancellation_token
  );

  /// <summary>
  ///   All objects under <paramref name="prefix" />, keyed by key.
  /// </summary>
  Task<IReadOnlyDictionary<string, RemoteObject>> ListAsync(
    string bucket,
    string prefix,
    CancellationToken cancellation_token
  );

  /// <summary>
  ///   Cheap request that proves the bucket is reachable (routing,
  ///   credentials). Returns a human readable result; throws if unreachable.
  /// </summary>
  Task<string> ProbeAsync(string bucket,
                          string prefix,
                          CancellationToken cancellation_token
  );
}
