using System.Collections.Concurrent;
using PackRat.Storage;

namespace PackRat.Tests.Sync;

/// <summary>In-memory bucket that records what was uploaded.</summary>
internal sealed class FakeObjectStore : IObjectStore {
  public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

  public ConcurrentQueue<ObjectUpload> Uploads { get; } = new();

  public Dictionary<string, RemoteObject> Listing { get; } = [];

  /// <summary>Runs while an upload is "in flight".</summary>
  public Action<ObjectUpload>? DuringUpload { get; set; }

  public Func<ObjectUpload, Exception?>? FailUpload { get; set; }

  /// <summary>Overrides the size HEAD reports.</summary>
  public long? ReportedSize { get; set; }

  public static string Id(string bucket, string key) {
    return $"{bucket}/{key}";
  }

  public async Task UploadAsync(ObjectUpload upload,
                                CancellationToken cancellation_token
  ) {
    Exception? failure = FailUpload?.Invoke(upload);
    if (failure is not null) {
      throw failure;
    }

    byte[] content = await File.ReadAllBytesAsync(
      upload.FilePath,
      cancellation_token
    );
    DuringUpload?.Invoke(upload);
    Objects[Id(upload.Bucket, upload.Key)] = content;
    Uploads.Enqueue(upload);
  }

  public Task<long?> GetObjectSizeAsync(string bucket,
                                        string key,
                                        CancellationToken cancellation_token
  ) {
    long? size = Objects.TryGetValue(Id(bucket, key), out byte[]? content)
      ? content.LongLength
      : null;
    return Task.FromResult(ReportedSize ?? size);
  }

  public Task<IReadOnlyDictionary<string, RemoteObject>> ListAsync(
    string bucket,
    string prefix,
    CancellationToken cancellation_token
  ) {
    return Task.FromResult<IReadOnlyDictionary<string, RemoteObject>>(
      Listing
    );
  }

  public Task<string> ProbeAsync(string bucket,
                                 string prefix,
                                 CancellationToken cancellation_token
  ) {
    return Task.FromResult("reachable");
  }
}
