namespace PackRat.Networking;

public sealed class NoProxyList {
  private static readonly char[] S_SEPARATORS_ = [
    ',',
    ' ',
    '\t',
    '\n',
    '\r',
    ';'
  ];

  private NoProxyList(IReadOnlyList<NoProxyRule> rules,
                      IReadOnlyList<string> invalid
  ) {
    Rules = rules;
    Invalid = invalid;
    HasNetworkRules = rules.Any(r => r.IsNetwork);
  }

  public static NoProxyList Empty { get; } = new([], []);

  public IReadOnlyList<NoProxyRule> Rules { get; }

  /// <summary>
  ///   Entries that could not be parsed (reported, then ignored).
  /// </summary>
  public IReadOnlyList<string> Invalid { get; }

  public bool HasNetworkRules { get; }

  /// <summary>
  ///   Parses entries, each of which may itself be a comma separated list
  ///   (the format of the <c>no_proxy</c> environment variable).
  /// </summary>
  public static NoProxyList Parse(IEnumerable<string?> entries) {
    List<NoProxyRule> rules = [];
    List<string> invalid = [];
    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

    foreach (string? entry in entries) {
      if (string.IsNullOrWhiteSpace(entry)) {
        continue;
      }

      foreach (string part in entry.Split(
                 S_SEPARATORS_,
                 StringSplitOptions.RemoveEmptyEntries
               )) {
        if (!seen.Add(part)) {
          continue;
        }

        if (NoProxyRule.TryParse(part, out NoProxyRule? rule)) {
          rules.Add(rule);
        } else {
          invalid.Add(part);
        }
      }
    }

    return new NoProxyList(rules, invalid);
  }
}
