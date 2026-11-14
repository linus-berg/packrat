using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace PackRat.Sync;

/// <summary>Size and modification time; used to detect changes.</summary>
public readonly record struct FileStamp(long Length,
                                        DateTimeOffset LastWriteTimeUtc
) {
  public static FileStamp? Read(string path) {
    FileInfo info = new(path);
    if (!info.Exists) {
      return null;
    }

    return new FileStamp(
      info.Length,
      new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
    );
  }
}

/// <summary>A file or directory found below a sync root.</summary>
public sealed record LocalEntry(string FullPath,
                                string RelativePath,
                                bool IsDirectory,
                                FileStamp Stamp
);

public static class DirectoryScanner {
  /// <summary>
  ///   Every file and directory below <paramref name="root" />. Symbolic
  ///   links are neither followed nor returned, excluded directories are
  ///   neither returned nor descended into, and unreadable directories are
  ///   skipped. Files are returned whether excluded or not.
  /// </summary>
  public static IEnumerable<LocalEntry> Enumerate(SyncRoot root) {
    EnumerationOptions options = new() {
      RecurseSubdirectories = true,
      IgnoreInaccessible = true,
      AttributesToSkip = 0,
      ReturnSpecialDirectories = false
    };

    return new FileSystemEnumerable<LocalEntry>(
      root.Path,
      (ref FileSystemEntry entry) => {
        string full_path = entry.ToFullPath();
        return new LocalEntry(
          full_path,
          ObjectKeys.RelativePath(root.Path, full_path),
          entry.IsDirectory,
          new FileStamp(
            entry.IsDirectory ? 0 : entry.Length,
            entry.LastWriteTimeUtc
          )
        );
      },
      options
    ) {
      ShouldIncludePredicate = (ref FileSystemEntry entry) =>
        !IsSymbolicLink(ref entry) &&
        (!entry.IsDirectory || !IsExcludedDirectory(root, ref entry)),
      ShouldRecursePredicate = (ref FileSystemEntry entry) =>
        !IsSymbolicLink(ref entry) && !IsExcludedDirectory(root, ref entry)
    };
  }

  public static bool IsEmptyDirectory(string path) {
    EnumerationOptions options = new() {
      AttributesToSkip = 0,
      IgnoreInaccessible = false,
      RecurseSubdirectories = false
    };
    return !Directory.EnumerateFileSystemEntries(path, "*", options).Any();
  }

  private static bool IsExcludedDirectory(SyncRoot root,
                                          ref FileSystemEntry entry
  ) {
    return root.Excludes.IsExcluded(
      ObjectKeys.RelativePath(root.Path, entry.ToFullPath())
    );
  }

  private static bool IsSymbolicLink(ref FileSystemEntry entry) {
    return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
  }
}

/// <summary>Linux specifics the BCL doesn't expose.</summary>
public static class UnixFileSystem {
  private const int C_AT_FDCWD_ = -100;
  private const int C_AT_SYMLINK_NOFOLLOW_ = 0x100;
  private const uint C_STATX_TYPE_ = 0x1;
  private const int C_STX_MODE_OFFSET_ = 28;
  private const int C_STATX_SIZE_ = 256;
  private const int C_FILE_TYPE_MASK_ = 0xF000;
  private const int C_REGULAR_FILE_ = 0x8000;

  /// <summary>
  ///   True for a regular file, false for anything else (FIFOs, sockets,
  ///   devices, symlinks) or a missing file, null if it can't be determined.
  ///   Opening a FIFO for reading blocks forever, so the uploader never opens
  ///   a file before this says it is regular.
  /// </summary>
  public static bool? IsRegularFile(string path) {
    if (!OperatingSystem.IsLinux()) {
      return null;
    }

    byte[] buffer = new byte[C_STATX_SIZE_];
    int result;
    try {
      result = Statx(
        C_AT_FDCWD_,
        path,
        C_AT_SYMLINK_NOFOLLOW_,
        C_STATX_TYPE_,
        buffer
      );
    } catch (Exception e) when (e is DllNotFoundException
                                  or EntryPointNotFoundException) {
      return null;
    }

    if (result != 0) {
      return File.Exists(path) ? null : false;
    }

    ushort mode = MemoryMarshal.Read<ushort>(
      buffer.AsSpan(C_STX_MODE_OFFSET_, sizeof(ushort))
    );
    return (mode & C_FILE_TYPE_MASK_) == C_REGULAR_FILE_;
  }

  [DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
  private static extern int Statx(int dir_fd,
                                  [MarshalAs(UnmanagedType.LPUTF8Str)]
                                  string path,
                                  int flags,
                                  uint mask,
                                  [Out] byte[] buffer
  );
}
