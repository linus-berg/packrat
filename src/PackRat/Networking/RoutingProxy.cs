using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PackRat.Networking;

public sealed record RouteDecision(ProxyEndpoint? Proxy, string Reason) {
  public bool IsDirect => Proxy is null;

  public override string ToString() {
    return Proxy is null
      ? $"direct ({Reason})"
      : $"via proxy {Proxy.Display} ({Reason})";
  }
}

/// <summary>
///   <see cref="IWebProxy" /> that picks the proxy per destination and honours
///   no_proxy, including CIDR ranges and (optionally) the addresses a host
///   name resolves to. Decisions are cached briefly so DNS changes, such as
///   an IPsec tunnel coming up, are picked up within a minute.
/// </summary>
public sealed class RoutingProxy : IWebProxy {
  private static readonly TimeSpan S_DECISION_TTL_ = TimeSpan.FromMinutes(1);

  private readonly ProxySettings settings_;
  private readonly Func<string, IPAddress[]> resolve_;
  private readonly TimeProvider time_;
  private readonly ILogger<RoutingProxy> logger_;
  private readonly ConcurrentDictionary<string, CachedDecision> cache_ = new();
  private readonly ConcurrentDictionary<string, string> announced_ = new();

  public RoutingProxy(ProxySettings settings,
                      ILogger<RoutingProxy> logger,
                      TimeProvider? time = null,
                      Func<string, IPAddress[]>? resolve = null
  ) {
    settings_ = settings;
    logger_ = logger;
    time_ = time ?? TimeProvider.System;
    resolve_ = resolve ?? Dns.GetHostAddresses;
    Credentials = new ProxyCredentialLookup(settings);
  }

  public ProxySettings Settings => settings_;

  public ICredentials? Credentials { get; set; }

  /// <summary>Every destination routed so far and how.</summary>
  public IReadOnlyDictionary<string, RouteDecision> Routes =>
    cache_.ToDictionary(p => p.Key, p => p.Value.Decision);

  public Uri? GetProxy(Uri destination) {
    return Decide(destination).Proxy?.Uri;
  }

  public bool IsBypassed(Uri host) {
    return Decide(host).IsDirect;
  }

  public RouteDecision Decide(Uri destination) {
    string key = $"{destination.Scheme}://{destination.IdnHost}" +
                 $":{destination.Port}";
    DateTimeOffset now = time_.GetUtcNow();
    if (cache_.TryGetValue(key, out CachedDecision? cached) &&
        cached.ExpiresAt > now) {
      return cached.Decision;
    }

    RouteDecision decision = Compute(destination);
    cache_[key] = new CachedDecision(decision, now + S_DECISION_TTL_);
    Announce(key, decision);
    return decision;
  }

  private RouteDecision Compute(Uri destination) {
    if (!settings_.Enabled) {
      return new RouteDecision(null, "proxy disabled in config");
    }

    ProxyEndpoint? proxy = settings_.ForScheme(destination.Scheme);
    if (proxy is null) {
      return new RouteDecision(null, "no proxy configured");
    }

    string host = NoProxyRule.NormalizeHost(destination.IdnHost);
    int port = destination.Port;
    IPAddress? host_address = null;
    if (destination.HostNameType is UriHostNameType.IPv4
                                    or UriHostNameType.IPv6 &&
        IPAddress.TryParse(host, out IPAddress? literal)) {
      host_address = NoProxyRule.Normalize(literal);
    }

    if (IsLoopback(host, host_address)) {
      return new RouteDecision(null, "loopback");
    }

    foreach (NoProxyRule rule in settings_.NoProxy.Rules) {
      if (rule.MatchesHost(host, host_address, port)) {
        return new RouteDecision(null, $"no_proxy entry '{rule.Text}'");
      }
    }

    if (host_address is null &&
        settings_.ResolveHostnames &&
        settings_.NoProxy.HasNetworkRules) {
      RouteDecision? resolved = MatchResolvedAddresses(host, port);
      if (resolved is not null) {
        return resolved;
      }
    }

    return new RouteDecision(proxy, proxy.Source);
  }

  private RouteDecision? MatchResolvedAddresses(string host, int port) {
    IPAddress[] addresses;
    try {
      addresses = resolve_(host);
    } catch (SocketException e) {
      // The proxy may be the only thing able to resolve the name.
      logger_.LogDebug(
        "Could not resolve {Host} for no_proxy matching: {Error}",
        host,
        e.Message
      );
      return null;
    }

    foreach (IPAddress address in addresses) {
      foreach (NoProxyRule rule in settings_.NoProxy.Rules) {
        if (rule.MatchesResolvedAddress(address, port)) {
          return new RouteDecision(
            null,
            $"no_proxy entry '{rule.Text}' contains {address}"
          );
        }
      }
    }

    return null;
  }

  /// <summary>
  ///   Orders a host's addresses for connecting: addresses inside an IP/CIDR
  ///   no_proxy entry first. If a name resolves to both a tunnelled and a
  ///   non-tunnelled address, the connection then goes through the tunnel
  ///   that made it bypass the proxy in the first place.
  /// </summary>
  public IPAddress[] OrderForConnect(IPAddress[] addresses, int port) {
    if (addresses.Length < 2 ||
        !settings_.Enabled ||
        !settings_.ResolveHostnames ||
        !settings_.NoProxy.HasNetworkRules) {
      return addresses;
    }

    // OrderBy is stable, so the resolver's order is kept otherwise.
    return addresses.OrderBy(a => InNetworkRule(a, port) ? 0 : 1).ToArray();
  }

  private bool InNetworkRule(IPAddress address, int port) {
    foreach (NoProxyRule rule in settings_.NoProxy.Rules) {
      if (rule.MatchesResolvedAddress(address, port)) {
        return true;
      }
    }

    return false;
  }

  private static bool IsLoopback(string host, IPAddress? address) {
    if (address is not null) {
      return IPAddress.IsLoopback(address);
    }

    return host == "localhost" ||
           host.EndsWith(".localhost", StringComparison.Ordinal);
  }

  private void Announce(string key, RouteDecision decision) {
    string description = decision.ToString();
    if (announced_.TryGetValue(key, out string? previous) &&
        previous == description) {
      return;
    }

    announced_[key] = description;
    logger_.LogInformation(
      "Route to {Destination}: {Route}",
      key,
      description
    );
  }

  private sealed record CachedDecision(RouteDecision Decision,
                                       DateTimeOffset ExpiresAt
  );

  /// <summary>Hands out the user:password from each proxy URL.</summary>
  private sealed class ProxyCredentialLookup : ICredentials {
    private readonly ProxyEndpoint?[] endpoints_;

    public ProxyCredentialLookup(ProxySettings settings) {
      endpoints_ = [
        settings.HttpProxy,
        settings.HttpsProxy
      ];
    }

    public NetworkCredential? GetCredential(Uri uri, string auth_type) {
      foreach (ProxyEndpoint? endpoint in endpoints_) {
        if (endpoint?.Credentials is not null &&
            Uri.Compare(
              endpoint.Uri,
              uri,
              UriComponents.HostAndPort,
              UriFormat.Unescaped,
              StringComparison.OrdinalIgnoreCase
            ) == 0) {
          return endpoint.Credentials;
        }
      }

      return null;
    }
  }
}
