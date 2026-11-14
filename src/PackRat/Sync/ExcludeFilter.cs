using Microsoft.Extensions.FileSystemGlobbing;

namespace PackRat.Sync;

/// <summary>
///   gitignore-flavoured exclusion. A pattern without a slash (<c>*.tmp</c>,
///   <c>.git</c>) matches a file or directory name at any depth. A pattern
///   with a slash (<c>cache/**</c>, <c>/build</c>) is anchored at the root of
///   the synced directory. Excluding a directory excludes everything in it.
/// </summary>
public sealed class ExcludeFilter {
  /// <summary>Files that are usually still being written.</summary>
  public static readonly IReadOnlyList<string> S_DEFAULT_PATTERNS = [
    "*.part",
    "*.partial",
    "*.crdownload",
    "*.download",
    "*.tmp",
    "*.temp",
    "*.swp",
    "*.swx",
    "*~",
    ".~lock.*#",
    ".goutputstream-*",
    ".nfs*",
    ".Trash-*"
  ];

  private readonly Matcher matcher_ = new(StringComparison.Ordinal);

  public ExcludeFilter(IEnumerable<string> patterns) {
    List<string> normalized = [];
    foreach (string raw in patterns) {
      string pattern = raw.Trim();
      if (pattern.Length == 0 || pattern.StartsWith('#')) {
        continue;
      }

      pattern = pattern.TrimEnd('/');
      if (pattern.Contains('/')) {
        pattern = pattern.TrimStart('/');
      } else {
        pattern = "**/" + pattern;
      }

      if (pattern.Length == 0) {
        continue;
      }

      normalized.Add(raw.Trim());
      matcher_.AddInclude(pattern);
      matcher_.AddInclude(pattern + "/**");
    }

    Patterns = normalized;
  }

  public static ExcludeFilter None { get; } = new([]);

  public IReadOnlyList<string> Patterns { get; }

  /// <param name="relative_path">
  ///   Path relative to the root, '/' separated.
  /// </param>
  public bool IsExcluded(string relative_path) {
    return Patterns.Count > 0 && matcher_.Match(relative_path).HasMatches;
  }
}
