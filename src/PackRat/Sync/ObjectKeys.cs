using System.Text;

namespace PackRat.Sync;

public static class ObjectKeys {
  public const int C_MAX_KEY_BYTES = 1024;

  /// <summary>
  ///   Trims slashes and collapses repeated ones: "/a//b/" → "a/b".
  /// </summary>
  public static string NormalizePrefix(string? prefix) {
    if (string.IsNullOrWhiteSpace(prefix)) {
      return string.Empty;
    }

    string[] segments = prefix.Trim()
                              .Split(
                                '/',
                                StringSplitOptions.RemoveEmptyEntries
                              );
    return string.Join('/', segments);
  }

  /// <summary>
  ///   <c>root/a/b/file.txt</c> with prefix <c>p</c> → <c>p/a/b/file.txt</c>;
  ///   without a prefix → <c>a/b/file.txt</c>.
  /// </summary>
  public static string ForFile(string root, string full_path, string prefix) {
    return ForRelativePath(prefix, RelativePath(root, full_path));
  }

  public static string ForRelativePath(string prefix, string relative_path) {
    return prefix.Length == 0 ? relative_path : prefix + "/" + relative_path;
  }

  public static string RelativePath(string root, string full_path) {
    string relative = Path.GetRelativePath(root, full_path);
    if (Path.DirectorySeparatorChar != '/') {
      relative = relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    return relative;
  }

  /// <summary>
  ///   The ListObjects prefix that covers everything under a root.
  /// </summary>
  public static string ListingPrefix(string prefix) {
    return prefix.Length == 0 ? string.Empty : prefix + "/";
  }

  public static bool IsValidLength(string key) {
    return Encoding.UTF8.GetByteCount(key) <= C_MAX_KEY_BYTES;
  }
}
