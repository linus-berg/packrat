using PackRat.Configuration;
using PackRat.Sync;

namespace PackRat.Tests.Sync;

public sealed class ObjectKeysTests {
  [Theory]
  [InlineData(null, "")]
  [InlineData("", "")]
  [InlineData("/", "")]
  [InlineData("backups", "backups")]
  [InlineData("/backups/", "backups")]
  [InlineData("a//b///c/", "a/b/c")]
  public void NormalizesPrefixes(string? prefix, string expected) {
    Assert.Equal(expected, ObjectKeys.NormalizePrefix(prefix));
  }

  [Fact]
  public void MirrorsTheRelativePathUnderThePrefix() {
    string key = ObjectKeys.ForFile(
      "/data/root",
      "/data/root/dir a/dir b/file x.txt",
      "my/prefix"
    );

    Assert.Equal("my/prefix/dir a/dir b/file x.txt", key);
  }

  [Fact]
  public void WithoutPrefixTheKeyIsTheRelativePath() {
    Assert.Equal(
      "dir a/file.txt",
      ObjectKeys.ForFile("/data/root", "/data/root/dir a/file.txt", "")
    );
  }

  [Fact]
  public void KeyLengthIsMeasuredInUtf8Bytes() {
    Assert.True(ObjectKeys.IsValidLength(new string('a', 1024)));
    Assert.False(ObjectKeys.IsValidLength(new string('å', 513)));
  }
}

public sealed class ExcludeFilterTests {
  [Theory]
  [InlineData("*.tmp", "file.tmp", true)]
  [InlineData("*.tmp", "deep/down/file.tmp", true)]
  [InlineData("*.tmp", "file.tmp.pdf", false)]
  [InlineData(".git", ".git/config", true)]
  [InlineData(".git", "src/.git/HEAD", true)]
  [InlineData(".git", ".gitignore", false)]
  [InlineData("cache/**", "cache/a/b.bin", true)]
  [InlineData("cache/**", "other/cache/b.bin", false)]
  [InlineData("/build", "build/out.o", true)]
  [InlineData("/build", "src/build/out.o", false)]
  [InlineData("node_modules/", "web/node_modules/x.js", true)]
  [InlineData("*.TMP", "file.tmp", false)]
  [InlineData("# comment", "# comment", false)]
  public void MatchesLikeGitignore(string pattern,
                                   string path,
                                   bool expected
  ) {
    ExcludeFilter filter = new([pattern]);

    Assert.Equal(expected, filter.IsExcluded(path));
  }

  [Theory]
  [InlineData("photo.jpg.part")]
  [InlineData("video.mp4.crdownload")]
  [InlineData("docs/.~lock.report.odt#")]
  [InlineData(".goutputstream-ABC123")]
  [InlineData("notes.txt~")]
  public void DefaultsSkipFilesThatAreStillBeingWritten(string path) {
    ExcludeFilter filter = new(ExcludeFilter.S_DEFAULT_PATTERNS);

    Assert.True(filter.IsExcluded(path));
    Assert.False(filter.IsExcluded("photo.jpg"));
  }
}

public sealed class DurationsTests {
  [Theory]
  [InlineData("500ms", 0.5)]
  [InlineData("30s", 30)]
  [InlineData("5m", 300)]
  [InlineData("1h30m", 5400)]
  [InlineData("1.5h", 5400)]
  [InlineData("2d", 172800)]
  [InlineData("00:01:30", 90)]
  [InlineData("0s", 0)]
  public void Parses(string text, double seconds) {
    Assert.True(Durations.TryParse(text, out TimeSpan duration));
    Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
  }

  [Theory]
  [InlineData("30")]
  [InlineData("")]
  [InlineData("5 minutes")]
  [InlineData("-5s")]
  [InlineData("5y")]
  public void RejectsAmbiguousOrInvalid(string text) {
    Assert.False(Durations.TryParse(text, out _));
  }
}
