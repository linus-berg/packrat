using System.Diagnostics;
using PackRat.Storage;
using PackRat.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace PackRat.Tests.Sync;

public sealed class SyncEngineTests : IDisposable {
  private static readonly TimeSpan S_SETTLE_ = TimeSpan.FromSeconds(30);

  private readonly string root_;
  private readonly FakeObjectStore store_ = new();
  private readonly FakeTimeProvider time_;

  public SyncEngineTests() {
    root_ = Directory.CreateTempSubdirectory("packrat-root-").FullName;
    time_ = new FakeTimeProvider(DateTimeOffset.UtcNow);
  }

  public void Dispose() {
    Directory.Delete(root_, true);
  }

  [Fact]
  public async Task MovesFilesKeepingTheirRelativePath() {
    WriteFile("dir a/dir b/file x.txt", "hello");
    WriteFile("top.bin", "top");
    SyncEngine engine = CreateEngine(Root("backups/laptop"));

    SyncPassResult result = await PassAsync(engine);

    Assert.Equal(2, result.Uploaded);
    Assert.Equal(2, result.Deleted);
    Assert.Equal(
      "hello"u8.ToArray(),
      store_.Objects["bucket/backups/laptop/dir a/dir b/file x.txt"]
    );
    Assert.True(store_.Objects.ContainsKey("bucket/backups/laptop/top.bin"));
    Assert.False(File.Exists(Path.Combine(root_, "dir a/dir b/file x.txt")));
    Assert.False(File.Exists(Path.Combine(root_, "top.bin")));
  }

  [Fact]
  public async Task WithoutPrefixKeysStartAtTheRoot() {
    WriteFile("a/b.txt", "x");
    SyncEngine engine = CreateEngine(Root(""));

    await PassAsync(engine);

    Assert.True(store_.Objects.ContainsKey("bucket/a/b.txt"));
  }

  [Fact]
  public async Task FilesThatAreStillBeingWrittenWait() {
    SyncEngine engine = CreateEngine(Root("p"));
    string path = WriteFile("fresh.txt", "x");
    DateTimeOffset now = time_.GetUtcNow();
    File.SetLastWriteTimeUtc(
      path,
      (now - TimeSpan.FromSeconds(5)).UtcDateTime
    );

    SyncPassResult first = await PassAsync(engine);
    time_.Advance(TimeSpan.FromSeconds(26));
    SyncPassResult second = await PassAsync(engine);

    Assert.Equal(0, first.Uploaded);
    Assert.Equal(1, first.Waiting);
    Assert.Equal(now + TimeSpan.FromSeconds(25), first.NextDueAt);
    Assert.Equal(1, second.Uploaded);
  }

  [Fact]
  public async Task ExcludedFilesAndDirectoriesStay() {
    WriteFile("movie.mkv.part", "partial");
    WriteFile(".git/config", "git");
    WriteFile("keep.txt", "keep");
    SyncEngine engine = CreateEngine(
      Root("p", excludes: [.. ExcludeFilter.S_DEFAULT_PATTERNS, ".git"])
    );

    SyncPassResult result = await PassAsync(engine);

    Assert.Equal(1, result.Uploaded);
    Assert.True(File.Exists(Path.Combine(root_, "movie.mkv.part")));
    Assert.True(File.Exists(Path.Combine(root_, ".git/config")));
  }

  [Fact]
  public async Task CopyModeUploadsOnceAndAgainAfterAChange() {
    string path = WriteFile("doc.txt", "v1");
    SyncEngine engine = CreateEngine(Root("p", delete: false));

    SyncPassResult first = await PassAsync(engine);
    SyncPassResult second = await PassAsync(engine);
    File.WriteAllText(path, "version 2");
    time_.Advance(TimeSpan.FromHours(1));
    SyncPassResult third = await PassAsync(engine);

    Assert.Equal(1, first.Uploaded);
    Assert.Equal(0, second.Uploaded);
    Assert.Equal(1, third.Uploaded);
    Assert.True(File.Exists(path));
    Assert.Equal("version 2"u8.ToArray(), store_.Objects["bucket/p/doc.txt"]);
  }

  [Fact]
  public async Task CopyModeSkipsFilesAlreadyInTheBucketAfterRestart() {
    string path = WriteFile("doc.txt", "same");
    FileInfo info = new(path);
    store_.Listing["p/doc.txt"] = new RemoteObject(
      info.Length,
      new DateTimeOffset(info.LastWriteTimeUtc) + TimeSpan.FromMinutes(1)
    );
    SyncEngine engine = CreateEngine(Root("p", delete: false));

    SyncPassResult result = await PassAsync(engine);

    Assert.Equal(0, result.Uploaded);
  }

  [Fact]
  public async Task FailedVerificationKeepsTheLocalFile() {
    string path = WriteFile("important.db", "precious");
    store_.ReportedSize = 3;
    SyncEngine engine = CreateEngine(Root("p"));

    SyncPassResult result = await PassAsync(engine);

    Assert.Equal(1, result.Failed);
    Assert.Equal(0, result.Deleted);
    Assert.True(File.Exists(path));
  }

  [Fact]
  public async Task FileChangedDuringUploadIsKeptAndUploadedAgain() {
    string path = WriteFile("growing.log", "first");
    bool changed = false;
    store_.DuringUpload = _ => {
      if (!changed) {
        changed = true;
        File.AppendAllText(path, " and more");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
      }
    };
    SyncEngine engine = CreateEngine(Root("p"));

    SyncPassResult first = await PassAsync(engine);
    Assert.True(File.Exists(path));
    Assert.Equal(0, first.Deleted);

    SyncPassResult second = await PassAsync(engine);

    Assert.Equal(1, second.Deleted);
    Assert.Equal(
      "first and more"u8.ToArray(),
      store_.Objects["bucket/p/growing.log"]
    );
  }

  [Fact]
  public async Task FailuresBackOffBeforeRetrying() {
    string path = WriteFile("a.txt", "x");
    int attempts = 0;
    store_.FailUpload = _ => {
      attempts++;
      return attempts < 3 ? new IOException("network down") : null;
    };
    SyncEngine engine = CreateEngine(Root("p"));

    SyncPassResult first = await PassAsync(engine);
    SyncPassResult immediate = await PassAsync(engine);
    time_.Advance(TimeSpan.FromSeconds(31));
    SyncPassResult second = await PassAsync(engine);
    time_.Advance(TimeSpan.FromSeconds(30));
    SyncPassResult too_soon = await PassAsync(engine);
    time_.Advance(TimeSpan.FromSeconds(31));
    SyncPassResult third = await PassAsync(engine);

    Assert.Equal(1, first.Failed);
    Assert.Equal(0, immediate.Failed + immediate.Uploaded);
    Assert.NotNull(immediate.NextDueAt);
    Assert.Equal(1, second.Failed);
    Assert.Equal(0, too_soon.Failed + too_soon.Uploaded);
    Assert.Equal(1, third.Uploaded);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task DryRunChangesNothing() {
    string path = WriteFile("a/b.txt", "x");
    SyncEngine engine = CreateEngine(Root("p"), dry_run: true);

    await PassAsync(engine);
    time_.Advance(TimeSpan.FromHours(1));
    await PassAsync(engine);

    Assert.Empty(store_.Objects);
    Assert.True(File.Exists(path));
  }

  [Fact]
  public async Task EmptyDirectoriesAreRemovedAfterTheSettleTime() {
    WriteFile("x/y/z/file.txt", "x");
    Directory.CreateDirectory(Path.Combine(root_, "x/sibling"));
    SyncEngine engine = CreateEngine(Root("p"));

    await PassAsync(engine);
    Assert.True(Directory.Exists(Path.Combine(root_, "x/y/z")));

    // Each level becomes empty (and gets a fresh mtime) once its child goes.
    for (int i = 0; i < 4; i++) {
      time_.Advance(TimeSpan.FromHours(1));
      await PassAsync(engine);
    }

    Assert.False(Directory.Exists(Path.Combine(root_, "x")));
    Assert.True(Directory.Exists(root_));
  }

  [Fact]
  public async Task SymlinksAreNeitherFollowedNorDeleted() {
    string outside = Directory.CreateTempSubdirectory("packrat-outside-")
                              .FullName;
    try {
      string target = Path.Combine(outside, "secret.txt");
      File.WriteAllText(target, "do not touch");
      File.CreateSymbolicLink(Path.Combine(root_, "link.txt"), target);
      Directory.CreateSymbolicLink(Path.Combine(root_, "linked-dir"), outside);
      SyncEngine engine = CreateEngine(Root("p"));

      SyncPassResult result = await PassAsync(engine);

      Assert.Equal(0, result.Uploaded);
      Assert.True(File.Exists(target));
      Assert.True(File.Exists(Path.Combine(root_, "link.txt")));
    } finally {
      Directory.Delete(outside, true);
    }
  }

  [Fact]
  public async Task FifosAreSkippedInsteadOfBlocking() {
    string fifo = Path.Combine(root_, "pipe");
    using (Process? mkfifo = Process.Start("mkfifo", [fifo])) {
      await mkfifo!.WaitForExitAsync(Cancellation);
    }

    File.SetLastWriteTimeUtc(fifo, DateTime.UtcNow - TimeSpan.FromHours(1));
    SyncEngine engine = CreateEngine(Root("p"));

    Task<SyncPassResult> pass = PassAsync(engine);
    Task finished = await Task.WhenAny(
      pass,
      Task.Delay(TimeSpan.FromSeconds(10), Cancellation)
    );

    Assert.Same(pass, finished);
    Assert.Equal(0, (await pass).Uploaded);
  }

  [Fact]
  public async Task FilesOpenForWritingAreLeftAlone() {
    string path = WriteFile("recording.wav", "still recording");
    SyncEngine engine = CreateEngine(Root("p"));

    SyncPassResult while_open;
    using (FileStream writer = new(path, FileMode.Append, FileAccess.Write)) {
      while_open = await PassAsync(engine);
    }

    SyncPassResult after_close = await PassAsync(engine);

    Assert.Equal(0, while_open.Uploaded);
    Assert.Equal(1, while_open.Waiting);
    Assert.Equal(1, after_close.Uploaded);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task FilesOpenOnlyForReadingAreMoved() {
    string path = WriteFile("viewer-has-it-open.pdf", "pdf");
    SyncEngine engine = CreateEngine(Root("p"));

    SyncPassResult result;
    using (FileStream reader = new(
             path,
             FileMode.Open,
             FileAccess.Read,
             FileShare.ReadWrite | FileShare.Delete
           )) {
      result = await PassAsync(engine);
    }

    Assert.Equal(1, result.Uploaded);
  }

  [Fact]
  public async Task SeveralRootsGoToTheirOwnDestinations() {
    string second_root = Directory.CreateTempSubdirectory("packrat-root2-")
                                  .FullName;
    try {
      WriteFile("one.txt", "1");
      File.WriteAllText(Path.Combine(second_root, "two.txt"), "2");
      File.SetLastWriteTimeUtc(
        Path.Combine(second_root, "two.txt"),
        DateTime.UtcNow - TimeSpan.FromHours(1)
      );
      SyncEngine engine = CreateEngine(
        Root("first"),
        Root("second", path: second_root, bucket: "other")
      );

      SyncPassResult result = await PassAsync(engine);

      Assert.Equal(2, result.Uploaded);
      Assert.True(store_.Objects.ContainsKey("bucket/first/one.txt"));
      Assert.True(store_.Objects.ContainsKey("other/second/two.txt"));
    } finally {
      Directory.Delete(second_root, true);
    }
  }

  [Fact]
  public async Task MissingRootIsSkippedUntilItAppears() {
    string missing = Path.Combine(root_, "later");
    SyncEngine engine = CreateEngine(Root("p", path: missing));

    SyncPassResult before = await PassAsync(engine);
    Directory.CreateDirectory(missing);
    File.WriteAllText(Path.Combine(missing, "f.txt"), "x");
    File.SetLastWriteTimeUtc(
      Path.Combine(missing, "f.txt"),
      DateTime.UtcNow - TimeSpan.FromHours(1)
    );
    SyncPassResult after = await PassAsync(engine);

    Assert.Equal(0, before.Uploaded);
    Assert.Equal(1, after.Uploaded);
  }

  private SyncRoot Root(string prefix,
                        bool delete = true,
                        string? path = null,
                        string bucket = "bucket",
                        string[]? excludes = null
  ) {
    return new SyncRoot(
      path ?? root_,
      bucket,
      prefix,
      delete,
      null,
      new ExcludeFilter(excludes ?? [])
    );
  }

  private SyncEngine CreateEngine(params SyncRoot[] roots) {
    return CreateEngine(roots, false);
  }

  private SyncEngine CreateEngine(SyncRoot root, bool dry_run) {
    return CreateEngine([root], dry_run);
  }

  private SyncEngine CreateEngine(SyncRoot[] roots, bool dry_run) {
    SyncSettings settings = new(
      TimeSpan.FromMinutes(1),
      S_SETTLE_,
      4,
      true,
      true,
      false,
      dry_run
    );
    return new SyncEngine(
      roots,
      settings,
      store_,
      time_,
      NullLogger<SyncEngine>.Instance
    );
  }

  private static CancellationToken Cancellation =>
    TestContext.Current.CancellationToken;

  private static Task<SyncPassResult> PassAsync(SyncEngine engine) {
    return engine.RunOnceAsync(Cancellation);
  }

  /// <summary>Writes a file that was last modified an hour ago.</summary>
  private string WriteFile(string relative_path, string content) {
    string path = Path.Combine(root_, relative_path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
    return path;
  }
}
