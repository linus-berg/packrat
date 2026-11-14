using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace PackRat.Networking;

/// <summary>
///   One entry of a <c>no_proxy</c> list. Follows the conventions shared by
///   curl, wget and Go:
///   <list type="bullet">
///     <item><c>*</c> bypasses the proxy for everything.</item>
///     <item>
///       <c>example.com</c>, <c>.example.com</c> and <c>*.example.com</c>
///       all match <c>example.com</c> and every subdomain of it.
///     </item>
///     <item><c>10.1.2.3</c> and <c>fd00::1</c> match that address.</item>
///     <item>
///       <c>10.0.0.0/8</c> and <c>fd00::/8</c> match every address in the
///       range (for example the subnets routed through an IPsec tunnel).
///     </item>
///     <item>
///       An optional <c>:port</c> (<c>[v6]:port</c> for IPv6) restricts the
///       entry to that port.
///     </item>
///   </list>
/// </summary>
public sealed class NoProxyRule {
  private readonly string? domain_;
  private readonly IPAddress? network_;
  private readonly int prefix_length_;

  private NoProxyRule(string text,
                      bool matches_everything,
                      string? domain,
                      IPAddress? network,
                      int prefix_length,
                      int? port
  ) {
    Text = text;
    MatchesEverything = matches_everything;
    domain_ = domain;
    network_ = network;
    prefix_length_ = prefix_length;
    Port = port;
  }

  /// <summary>The entry as written in the configuration.</summary>
  public string Text { get; }

  public bool MatchesEverything { get; }

  /// <summary>True for IP address and CIDR entries.</summary>
  public bool IsNetwork => network_ is not null;

  public int? Port { get; }

  public override string ToString() {
    return Text;
  }

  public static bool TryParse(string? raw,
                              [NotNullWhen(true)] out NoProxyRule? rule
  ) {
    rule = null;
    string text = raw?.Trim() ?? string.Empty;
    if (text.Length == 0) {
      return false;
    }

    if (text == "*") {
      rule = new NoProxyRule(text, true, null, null, 0, null);
      return true;
    }

    if (!TrySplitPort(text, out string host, out int? port)) {
      return false;
    }

    if (TryParseNetwork(host, out IPAddress? network, out int prefix_length)) {
      rule = new NoProxyRule(text, false, null, network, prefix_length, port);
      return true;
    }

    string domain = NormalizeHost(host);
    if (domain.StartsWith("*.", StringComparison.Ordinal)) {
      domain = domain[2..];
    } else if (domain.StartsWith('.')) {
      domain = domain[1..];
    }

    if (domain.Length == 0 || domain.Contains('*') || domain.Contains('/')) {
      return false;
    }

    rule = new NoProxyRule(text, false, domain, null, 0, port);
    return true;
  }

  /// <summary>Matches against the host and port of the request URI.</summary>
  public bool MatchesHost(string normalized_host,
                          IPAddress? host_address,
                          int port
  ) {
    if (MatchesEverything) {
      return true;
    }

    if (Port is int required_port && required_port != port) {
      return false;
    }

    if (network_ is not null) {
      return host_address is not null && Contains(host_address);
    }

    return normalized_host == domain_ ||
           normalized_host.EndsWith("." + domain_, StringComparison.Ordinal);
  }

  /// <summary>
  ///   Matches an address the destination host name resolved to. Only
  ///   network entries can match resolved addresses.
  /// </summary>
  public bool MatchesResolvedAddress(IPAddress address, int port) {
    if (network_ is null) {
      return false;
    }

    if (Port is int required_port && required_port != port) {
      return false;
    }

    return Contains(address);
  }

  public static string NormalizeHost(string host) {
    string normalized = host.Trim().ToLowerInvariant();
    if (normalized.StartsWith('[') && normalized.EndsWith(']')) {
      normalized = normalized[1..^1];
    }

    return normalized.TrimEnd('.');
  }

  public static IPAddress Normalize(IPAddress address) {
    return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
  }

  private bool Contains(IPAddress candidate) {
    IPAddress address = Normalize(candidate);
    if (network_ is null || address.AddressFamily != network_.AddressFamily) {
      return false;
    }

    byte[] expected = network_.GetAddressBytes();
    byte[] actual = address.GetAddressBytes();
    int full_bytes = prefix_length_ / 8;
    for (int i = 0; i < full_bytes; i++) {
      if (expected[i] != actual[i]) {
        return false;
      }
    }

    int remaining_bits = prefix_length_ % 8;
    if (remaining_bits == 0) {
      return true;
    }

    int mask = (0xFF << (8 - remaining_bits)) & 0xFF;
    return (expected[full_bytes] & mask) == (actual[full_bytes] & mask);
  }

  private static bool TrySplitPort(string text,
                                   out string host,
                                   out int? port
  ) {
    host = text;
    port = null;

    if (text.StartsWith('[')) {
      int close = text.IndexOf(']');
      if (close < 0) {
        return false;
      }

      host = text[1..close];
      string rest = text[(close + 1)..];
      if (rest.Length == 0) {
        return true;
      }

      if (rest.StartsWith('/')) {
        host += rest;
        return true;
      }

      return rest.StartsWith(':') && TryParsePort(rest[1..], out port);
    }

    int first_colon = text.IndexOf(':');
    if (first_colon < 0 || first_colon != text.LastIndexOf(':')) {
      // No colon at all, or several colons (a bare IPv6 address).
      return true;
    }

    host = text[..first_colon];
    return TryParsePort(text[(first_colon + 1)..], out port);
  }

  private static bool TryParsePort(string text, out int? port) {
    port = null;
    if (!int.TryParse(text, out int value) || value is < 1 or > 65535) {
      return false;
    }

    port = value;
    return true;
  }

  private static bool TryParseNetwork(string host,
                                      out IPAddress? network,
                                      out int prefix_length
  ) {
    network = null;
    prefix_length = 0;

    string address_text = host;
    int? requested_prefix = null;
    int slash = host.IndexOf('/');
    if (slash >= 0) {
      address_text = host[..slash];
      if (!int.TryParse(host[(slash + 1)..], out int parsed_prefix)) {
        return false;
      }

      requested_prefix = parsed_prefix;
    }

    if (!IsIpLiteral(address_text) ||
        !IPAddress.TryParse(address_text, out IPAddress? address)) {
      return false;
    }

    address = Normalize(address);
    int max_prefix = address.AddressFamily == AddressFamily.InterNetwork
      ? 32
      : 128;
    int prefix = requested_prefix ?? max_prefix;
    if (prefix < 0 || prefix > max_prefix) {
      return false;
    }

    network = address;
    prefix_length = prefix;
    return true;
  }

  /// <summary>
  ///   <see cref="IPAddress.TryParse(string, out IPAddress)" /> accepts
  ///   shorthand such as <c>10.1</c> or <c>42</c>; no_proxy entries only
  ///   treat dotted quads and IPv6 literals as addresses.
  /// </summary>
  private static bool IsIpLiteral(string text) {
    if (text.Contains(':')) {
      return true;
    }

    int dots = 0;
    foreach (char c in text) {
      if (c == '.') {
        dots++;
      } else if (!char.IsAsciiDigit(c)) {
        return false;
      }
    }

    return dots == 3;
  }
}
