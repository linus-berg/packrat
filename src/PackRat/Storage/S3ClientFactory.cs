using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using PackRat.Configuration;
using PackRat.Networking;

namespace PackRat.Storage;

public static class S3ClientFactory {
  public static AmazonS3Config CreateConfig(ResolvedAws aws,
                                            RoutingProxy proxy
  ) {
    AmazonS3Config config = new() {
      HttpClientFactory = new RoutingHttpClientFactory(proxy),
      RetryMode = RequestRetryMode.Standard,
      MaxErrorRetry = aws.MaxRetries,
      ConnectTimeout = aws.ConnectTimeout,
      Timeout = aws.RequestTimeout,
      RequestChecksumCalculation = aws.ChecksumsWhenRequired
        ? RequestChecksumCalculation.WHEN_REQUIRED
        : RequestChecksumCalculation.WHEN_SUPPORTED,
      ResponseChecksumValidation = aws.ChecksumsWhenRequired
        ? ResponseChecksumValidation.WHEN_REQUIRED
        : ResponseChecksumValidation.WHEN_SUPPORTED
    };

    string? region = aws.Region ?? RegionFromProfile(aws.Profile);
    if (aws.ServiceUrl is not null) {
      config.ServiceURL = aws.ServiceUrl;
      config.ForcePathStyle = aws.ForcePathStyle;
      if (region is not null) {
        config.AuthenticationRegion = region;
      }
    } else if (region is not null) {
      config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
    }

    return config;
  }

  public static IAmazonS3 Create(ResolvedAws aws, RoutingProxy proxy) {
    AmazonS3Config config = CreateConfig(aws, proxy);
    if (aws.Profile is null) {
      // Default chain: env vars, AWS_PROFILE, [default], SSO, ...
      return new AmazonS3Client(config);
    }

    CredentialProfileStoreChain chain = new();
    if (!chain.TryGetAWSCredentials(
          aws.Profile,
          out AWSCredentials? credentials
        )) {
      throw new ConfigurationException(
        $"AWS profile '{aws.Profile}' was not found in ~/.aws/config or " +
        "~/.aws/credentials."
      );
    }

    return new AmazonS3Client(credentials, config);
  }

  private static string? RegionFromProfile(string? profile) {
    if (profile is null) {
      return null;
    }

    CredentialProfileStoreChain chain = new();
    return chain.TryGetProfile(profile, out CredentialProfile? found)
      ? found.Region?.SystemName
      : null;
  }
}
