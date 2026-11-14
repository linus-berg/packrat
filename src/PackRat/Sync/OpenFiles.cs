using System.Globalization;

namespace PackRat.Sync;

/// <summary>
///   Finds files another process still has open for writing, using
///   /proc/&lt;pid&gt;/fd and fdinfo. A program that writes slower than the
///   settle time (a log appended every few minutes, a long recording) would
///   otherwise have its file uploaded half-written and deleted under it,
///   losing every later write. Only processes the service user can inspect
///   are seen, which covers that user's own programs.
/// </summary>
public static class OpenFiles {
  private const string C_PROC_ = "/proc";
  private const int C_ACCESS_MODE_MASK_ = 0x3;

  /// <summary>
  ///   The subset of <paramref name="paths" /> open for writing.
  /// </summary>
  public static HashSet<string> OpenForWriting(IEnumerable<string> paths) {
    HashSet<string> candidates = new(paths, StringComparer.Ordinal);
    HashSet<string> busy = new(StringComparer.Ordinal);
    if (candidates.Count == 0 || !Directory.Exists(C_PROC_)) {
      return busy;
    }

    foreach (string process in Directory.EnumerateDirectories(C_PROC_)) {
      if (!int.TryParse(Path.GetFileName(process), out _)) {
        continue;
      }

      ScanProcess(process, candidates, busy);
      if (busy.Count == candidates.Count) {
        break;
      }
    }

    return busy;
  }

  private static void ScanProcess(string process,
                                  HashSet<string> candidates,
                                  HashSet<string> busy
  ) {
    IEnumerable<string> descriptors;
    try {
      descriptors = Directory.EnumerateFileSystemEntries(
        Path.Combine(process, "fd")
      );
      foreach (string descriptor in descriptors) {
        string? target = new FileInfo(descriptor).LinkTarget;
        if (target is null ||
            busy.Contains(target) ||
            !candidates.Contains(target)) {
          continue;
        }

        string info = Path.Combine(
          process,
          "fdinfo",
          Path.GetFileName(descriptor)
        );
        if (IsOpenForWriting(info)) {
          busy.Add(target);
        }
      }
    } catch (Exception e) when (e is IOException
                                  or UnauthorizedAccessException) {
      // Another user's process, or it exited while we looked.
    }
  }

  private static bool IsOpenForWriting(string fdinfo_path) {
    foreach (string line in File.ReadLines(fdinfo_path)) {
      if (!line.StartsWith("flags:", StringComparison.Ordinal)) {
        continue;
      }

      string octal = line["flags:".Length..].Trim();
      try {
        int flags = Convert.ToInt32(octal, 8);
        // O_WRONLY = 1, O_RDWR = 2.
        return (flags & C_ACCESS_MODE_MASK_) != 0;
      } catch (FormatException) {
        return false;
      }
    }

    return false;
  }
}
