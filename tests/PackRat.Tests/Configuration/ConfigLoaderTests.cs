using PackRat.Configuration;
using PackRat.Sync;

namespace PackRat.Tests.Configuration;

public sealed class ConfigLoaderTests : IDisposable {
  private readonly string directory_ = Directory.CreateTempSubdirectory(
                                                 "packrat-config-"
                                               )
                                               .FullName;

  public void Dispose() {
    Directory.Delete(directory_, true);
  }

  [Fact]
  public void ReadsJsonWithCommentsAndResolvesDirectories() {
    string path = Write(
      """
      {
        // Default bucket.
        "bucket": "main-bucket",
        "aws": { "region": "eu-north-1", "checksums": "WhenRequired" },
        "sync": { "settleTime": "10s", "exclude": [ "*.log" ] },
        "directories": [
          { "path": "~/Scans", "prefix": "/scans/" },
          {
            "path": "/srv/photos/",
            "bucket": "photos",
            "deleteAfterUpload": false,
            "storageClass": "STANDARD_IA",
          },
        ]
      }
      """
    );

    ResolvedConfig config = ConfigLoader.Resolve(
      ConfigLoader.Read(path),
      path,
      false,
      "/home/tester"
    );

    Assert.Equal(TimeSpan.FromSeconds(10), config.Sync.SettleTime);
    Assert.True(config.Aws.ChecksumsWhenRequired);
    Assert.Equal("eu-north-1", config.Aws.Region);
    Assert.Collection(
      config.Roots,
      scans => {
        Assert.Equal("/home/tester/Scans", scans.Path);
        Assert.Equal("main-bucket", scans.Bucket);
        Assert.Equal("scans", scans.Prefix);
        Assert.True(scans.DeleteAfterUpload);
        Assert.True(scans.Excludes.IsExcluded("x/app.log"));
        Assert.True(scans.Excludes.IsExcluded("download.part"));
      },
      photos => {
        Assert.Equal("/srv/photos", photos.Path);
        Assert.Equal("photos", photos.Bucket);
        Assert.Equal("", photos.Prefix);
        Assert.False(photos.DeleteAfterUpload);
        Assert.Equal("STANDARD_IA", photos.StorageClass);
      }
    );
  }

  [Fact]
  public void UnknownKeysAreReportedInsteadOfIgnored() {
    string path = Write(
      """
      { "bucket": "b", "sync": { "deleteAfterUplaod": false } }
      """
    );

    ConfigurationException error = Assert.Throws<ConfigurationException>(
      () => ConfigLoader.Read(path)
    );
    Assert.Contains("deleteAfterUplaod", error.Message);
  }

  [Fact]
  public void CollectsEveryProblemAtOnce() {
    PackRatOptions options = new() {
      Sync = new SyncOptions {
        ScanInterval = "60",
        MaxConcurrentUploads = 0
      },
      Directories = [
        new DirectoryOptions {
          Path = "relative/path",
          Bucket = "b"
        },
        new DirectoryOptions {
          Path = "/data/no-bucket"
        }
      ]
    };

    ConfigurationException error = Assert.Throws<ConfigurationException>(
      () => ConfigLoader.Resolve(options, "test.json", false, "/home/t")
    );

    Assert.Contains("sync.scanInterval '60' is not a duration", error.Message);
    Assert.Contains("maxConcurrentUploads", error.Message);
    Assert.Contains("must be absolute", error.Message);
    Assert.Contains("has no bucket", error.Message);
  }

  [Fact]
  public void RejectsOverlappingDirectories() {
    PackRatOptions options = new() {
      Bucket = "b",
      Directories = [
        new DirectoryOptions {
          Path = "/data"
        },
        new DirectoryOptions {
          Path = "/data/inner"
        }
      ]
    };

    ConfigurationException error = Assert.Throws<ConfigurationException>(
      () => ConfigLoader.Resolve(options, "test.json", false, "/home/t")
    );
    Assert.Contains("overlap", error.Message);
  }

  [Fact]
  public void WarnsWhenTwoDirectoriesShareADestination() {
    PackRatOptions options = new() {
      Bucket = "b",
      Directories = [
        new DirectoryOptions {
          Path = "/data/a",
          Prefix = "same"
        },
        new DirectoryOptions {
          Path = "/data/b",
          Prefix = "same/"
        }
      ]
    };

    ResolvedConfig config = ConfigLoader.Resolve(
      options,
      "test.json",
      false,
      "/home/t"
    );

    Assert.Single(config.Warnings);
  }

  [Fact]
  public void DefaultExcludesCanBeTurnedOff() {
    PackRatOptions options = new() {
      Bucket = "b",
      Sync = new SyncOptions {
        UseDefaultExcludes = false
      },
      Directories = [
        new DirectoryOptions {
          Path = "/data/a"
        }
      ]
    };

    ResolvedConfig config = ConfigLoader.Resolve(
      options,
      "test.json",
      false,
      "/home/t"
    );

    Assert.False(config.Roots[0].Excludes.IsExcluded("file.part"));
  }

  [Fact]
  public void ConfigPathComesFromArgumentThenEnvironment() {
    string path = Write("{}");
    Dictionary<string, string> environment = new() {
      [ConfigLoader.C_CONFIG_ENVIRONMENT_VARIABLE] = path
    };

    Assert.Equal(
      path,
      ConfigLoader.FindConfigFile(null, environment.GetValueOrDefault)
    );
    Assert.Throws<ConfigurationException>(
      () => ConfigLoader.FindConfigFile(
        Path.Combine(directory_, "missing.json"),
        environment.GetValueOrDefault
      )
    );
  }

  private string Write(string json) {
    string path = Path.Combine(directory_, $"{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    return path;
  }
}
