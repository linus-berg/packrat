using PackRat.Configuration;

namespace PackRat.Tests.Configuration;

/// <summary>
///   The config.json the RPM installs into /etc must stay valid.
/// </summary>
public sealed class ShippedConfigTests {
  [Fact]
  public void ParsesAndAsksForDirectories() {
    string path = Path.Combine(RepositoryRoot(), "packaging", "config.json");

    PackRatOptions options = ConfigLoader.Read(path);
    ConfigurationException error = Assert.Throws<ConfigurationException>(
      () => ConfigLoader.Resolve(options, path, false, "/home/tester")
    );

    Assert.Equal(
      $"Invalid {path}:{Environment.NewLine}  - No directories configured.",
      error.Message
    );
  }

  [Fact]
  public void ResolvesOnceADirectoryIsAdded() {
    string path = Path.Combine(RepositoryRoot(), "packaging", "config.json");
    PackRatOptions options = ConfigLoader.Read(path);
    options.Directories.Add(
      new DirectoryOptions {
        Path = "~/Scans",
        Prefix = "scans"
      }
    );

    ResolvedConfig config = ConfigLoader.Resolve(
      options,
      path,
      false,
      "/home/tester"
    );

    Assert.Equal("s3://my-bucket/scans/", config.Roots[0].Destination);
    Assert.True(config.Proxy.Enabled);
    Assert.Equal(TimeSpan.FromMinutes(15), config.Aws.RequestTimeout);
  }

  private static string RepositoryRoot() {
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(
             Path.Combine(directory.FullName, "PackRat.slnx")
           )) {
      directory = directory.Parent;
    }

    return directory?.FullName ??
           throw new InvalidOperationException("Repository root not found");
  }
}
