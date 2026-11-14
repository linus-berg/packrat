using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;

namespace PackRat.Storage;

public sealed class S3ObjectStore : IObjectStore, IDisposable {
  private readonly IAmazonS3 client_;
  private readonly TransferUtility transfer_;

  public S3ObjectStore(IAmazonS3 client) {
    client_ = client;
    transfer_ = new TransferUtility(
      client,
      new TransferUtilityConfig {
        ConcurrentServiceRequests = 4
      }
    );
  }

  public async Task UploadAsync(ObjectUpload upload,
                                CancellationToken cancellation_token
  ) {
    TransferUtilityUploadRequest request = new() {
      BucketName = upload.Bucket,
      Key = upload.Key,
      FilePath = upload.FilePath,
      ContentType = AmazonS3Util.MimeTypeFromExtension(
        Path.GetExtension(upload.FilePath)
      )
    };

    if (!string.IsNullOrEmpty(upload.StorageClass)) {
      request.StorageClass = S3StorageClass.FindValue(upload.StorageClass);
    }

    // Same metadata key/format rclone uses, so tools can restore mtimes.
    double mtime = upload.LastWriteTimeUtc.ToUnixTimeMilliseconds() / 1000.0;
    request.Metadata.Add(
      "mtime",
      mtime.ToString("0.###", CultureInfo.InvariantCulture)
    );

    await transfer_.UploadAsync(request, cancellation_token);
  }

  public async Task<long?> GetObjectSizeAsync(string bucket,
                                              string key,
                                              CancellationToken
                                                cancellation_token
  ) {
    try {
      GetObjectMetadataResponse response = await client_.GetObjectMetadataAsync(
        new GetObjectMetadataRequest {
          BucketName = bucket,
          Key = key
        },
        cancellation_token
      );
      return response.ContentLength;
    } catch (AmazonS3Exception e)
      when (e.StatusCode == HttpStatusCode.NotFound) {
      return null;
    }
  }

  public async Task<IReadOnlyDictionary<string, RemoteObject>> ListAsync(
    string bucket,
    string prefix,
    CancellationToken cancellation_token
  ) {
    Dictionary<string, RemoteObject> objects = new(StringComparer.Ordinal);
    ListObjectsV2Request request = new() {
      BucketName = bucket,
      Prefix = prefix.Length == 0 ? null : prefix
    };

    IAsyncEnumerable<S3Object> pages = client_.Paginators
                                              .ListObjectsV2(request)
                                              .S3Objects;
    await foreach (S3Object s3_object in pages.WithCancellation(
                     cancellation_token
                   )) {
      objects[s3_object.Key] = new RemoteObject(
        s3_object.Size ?? 0,
        s3_object.LastModified is DateTime modified
          ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero)
          : DateTimeOffset.MinValue
      );
    }

    return objects;
  }

  public async Task<string> ProbeAsync(string bucket,
                                       string prefix,
                                       CancellationToken cancellation_token
  ) {
    try {
      await client_.ListObjectsV2Async(
        new ListObjectsV2Request {
          BucketName = bucket,
          Prefix = prefix.Length == 0 ? null : prefix,
          MaxKeys = 1
        },
        cancellation_token
      );
      return "reachable";
    } catch (AmazonS3Exception e)
      when (e.StatusCode == HttpStatusCode.Forbidden) {
      // S3 answered, so routing and credentials work.
      return $"reachable, but listing is denied ({e.ErrorCode}); fine " +
             "unless a directory has deleteAfterUpload set to false";
    }
  }

  public void Dispose() {
    transfer_.Dispose();
  }
}
