using System.Net;
using PackRat.Configuration;

namespace PackRat.Networking;

/// <summary>A proxy URL plus where it came from (for diagnostics).</summary>
public sealed record ProxyEndpoint(Uri Uri,
                                   NetworkCredential? Credentials,
                                   string Source
) {
  /// <summary>The URL without user info, safe to log.</summary>
  public string Display => Uri.GetLeftPart(UriPartial.Authority);
}

/// <summary>
///   Effective proxy configuration after merging config and env.
/// </summary>
public sealed class ProxySettings {
  private static readonly string[] S_SUPPORTED_SCHEMES_ = [
    "http",
    "https",
    "socks4",
    "socks4a",
    "socks5"
  ];

  public ProxySettings(bool enabled,
                       ProxyEndpoint? http_proxy,
                       ProxyEndpoint? https_proxy,
                       NoProxyList no_proxy,
                       bool resolve_hostnames
  ) {
    Enabled = enabled;
    HttpProxy = http_proxy;
    HttpsProxy = https_proxy;
    NoProxy = no_proxy;
    ResolveHostnames = resolve_hostnames;
  }

  public static ProxySettings Direct { get; } = new(
    false,
    null,
    null,
    NoProxyList.Empty,
    false
  );

  public bool Enabled { get; }

  public ProxyEndpoint? HttpProxy { get; }

  public ProxyEndpoint? HttpsProxy { get; }

  public NoProxyList NoProxy { get; }

  public bool ResolveHostnames { get; }

  public ProxyEndpoint? ForScheme(string scheme) {
    return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
      ? HttpProxy
      : HttpsProxy;
  }

  public static ProxySettings Resolve(ProxyOptions options,
                                      Func<string, string?> get_environment
  ) {
    if (!options.Enabled) {
      return Direct;
    }

    ProxyEndpoint? http_proxy = null;
    ProxyEndpoint? https_proxy = null;
    List<string?> no_proxy = [];

    if (!string.IsNullOrWhiteSpace(options.Url)) {
      ProxyEndpoint configured = ParseProxy(options.Url, "config: proxy.url");
      http_proxy = configured;
      https_proxy = configured;
    } else if (options.UseEnvironment) {
      http_proxy = FromEnvironment(
        get_environment,
        "http_proxy",
        "HTTP_PROXY",
        "all_proxy",
        "ALL_PROXY"
      );
      https_proxy = FromEnvironment(
        get_environment,
        "https_proxy",
        "HTTPS_PROXY",
        "all_proxy",
        "ALL_PROXY"
      );
    }

    if (options.UseEnvironment) {
      no_proxy.Add(get_environment("no_proxy") ?? get_environment("NO_PROXY"));
    }

    no_proxy.AddRange(options.NoProxy);

    return new ProxySettings(
      true,
      http_proxy,
      https_proxy,
      NoProxyList.Parse(no_proxy),
      options.ResolveHostnames
    );
  }

  private static ProxyEndpoint? FromEnvironment(Func<string, string?> get,
                                                params string[] names
  ) {
    foreach (string name in names) {
      string? value = get(name);
      if (!string.IsNullOrWhiteSpace(value)) {
        return ParseProxy(value, $"env: {name}");
      }
    }

    return null;
  }

  public static ProxyEndpoint ParseProxy(string value, string source) {
    string text = value.Trim();
    if (!text.Contains("://", StringComparison.Ordinal)) {
      text = "http://" + text;
    }

    if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
        string.IsNullOrEmpty(uri.Host)) {
      throw new ConfigurationException(
        $"Invalid proxy URL '{Redact(value)}' ({source})."
      );
    }

    if (!S_SUPPORTED_SCHEMES_.Contains(uri.Scheme)) {
      throw new ConfigurationException(
        $"Unsupported proxy scheme '{uri.Scheme}' ({source}); use one of " +
        string.Join(", ", S_SUPPORTED_SCHEMES_) + "."
      );
    }

    NetworkCredential? credentials = null;
    if (!string.IsNullOrEmpty(uri.UserInfo)) {
      string[] parts = uri.UserInfo.Split(':', 2);
      credentials = new NetworkCredential(
        Uri.UnescapeDataString(parts[0]),
        parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty
      );
    }

    UriBuilder without_user_info = new(uri) {
      UserName = string.Empty,
      Password = string.Empty,
      Path = string.Empty
    };
    return new ProxyEndpoint(without_user_info.Uri, credentials, source);
  }

  private static string Redact(string value) {
    int at = value.LastIndexOf('@');
    int scheme = value.IndexOf("://", StringComparison.Ordinal);
    if (at < 0) {
      return value;
    }

    int start = scheme >= 0 ? scheme + 3 : 0;
    return value[..start] + "***" + value[at..];
  }
}
