using System.Net;
using System.Net.Sockets;
using PackRat.Configuration;
using PackRat.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace PackRat.Tests.Networking;

public sealed class RoutingProxyTests {
  private static readonly Uri S_S3_ =
    new("https://my-bucket.s3.amazonaws.com/");

  [Fact]
  public void UsesHttpsProxyFromEnvironmentForHttps() {
    RoutingProxy proxy = Create(
      new ProxyOptions(),
      new Dictionary<string, string> {
        ["https_proxy"] = "http://secure-proxy:3128",
        ["http_proxy"] = "http://plain-proxy:3128"
      }
    );

    Assert.Equal(new Uri("http://secure-proxy:3128"), proxy.GetProxy(S_S3_));
    Assert.Equal(
      new Uri("http://plain-proxy:3128"),
      proxy.GetProxy(new Uri("http://minio.example.com:9000/"))
    );
  }

  [Fact]
  public void LowercaseWinsAndAllProxyIsTheFallback() {
    RoutingProxy lowercase = Create(
      new ProxyOptions(),
      new Dictionary<string, string> {
        ["https_proxy"] = "lower:1",
        ["HTTPS_PROXY"] = "upper:2"
      }
    );
    RoutingProxy fallback = Create(
      new ProxyOptions(),
      new Dictionary<string, string> {
        ["ALL_PROXY"] = "socks5://tunnel:1080"
      }
    );

    Assert.Equal(new Uri("http://lower:1"), lowercase.GetProxy(S_S3_));
    Assert.Equal(new Uri("socks5://tunnel:1080"), fallback.GetProxy(S_S3_));
  }

  [Fact]
  public void ConfigUrlOverridesEnvironment() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        Url = "http://configured:8080"
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://from-env:3128"
      }
    );

    Assert.Equal(new Uri("http://configured:8080"), proxy.GetProxy(S_S3_));
    Assert.Equal("config: proxy.url", proxy.Decide(S_S3_).Proxy?.Source);
  }

  [Fact]
  public void NoProxyFromEnvironmentAndConfigAreMerged() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        NoProxy = [
          ".internal"
        ]
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128",
        ["no_proxy"] = "amazonaws.com"
      }
    );

    Assert.True(proxy.IsBypassed(S_S3_));
    Assert.True(proxy.IsBypassed(new Uri("https://s3.internal/")));
    Assert.False(proxy.IsBypassed(new Uri("https://example.org/")));
  }

  [Fact]
  public void HostNameResolvingIntoTunnelSubnetGoesDirect() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        NoProxy = [
          "10.20.0.0/16"
        ]
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128"
      },
      host => host == "s3.tunnel.example.com"
        ? [
          IPAddress.Parse("10.20.1.5")
        ]
        : [
          IPAddress.Parse("52.1.2.3")
        ]
    );

    RouteDecision tunnel = proxy.Decide(
      new Uri("https://s3.tunnel.example.com/bucket")
    );
    RouteDecision internet = proxy.Decide(S_S3_);

    Assert.True(tunnel.IsDirect);
    Assert.Contains("10.20.1.5", tunnel.Reason);
    Assert.False(internet.IsDirect);
  }

  [Fact]
  public void ResolvingCanBeTurnedOff() {
    int lookups = 0;
    RoutingProxy proxy = Create(
      new ProxyOptions {
        NoProxy = [
          "10.0.0.0/8"
        ],
        ResolveHostnames = false
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128"
      },
      _ => {
        lookups++;
        return [
          IPAddress.Parse("10.1.1.1")
        ];
      }
    );

    Assert.False(proxy.IsBypassed(new Uri("https://s3.internal/")));
    Assert.True(proxy.IsBypassed(new Uri("https://10.1.1.1/")));
    Assert.Equal(0, lookups);
  }

  [Fact]
  public void UnresolvableHostUsesTheProxy() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        NoProxy = [
          "10.0.0.0/8"
        ]
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128"
      },
      _ => throw new SocketException((int)SocketError.HostNotFound)
    );

    Assert.False(proxy.IsBypassed(S_S3_));
  }

  [Fact]
  public void DecisionsAreCachedThenRefreshed() {
    int lookups = 0;
    Microsoft.Extensions.Time.Testing.FakeTimeProvider time = new();
    ProxySettings settings = ProxySettings.Resolve(
      new ProxyOptions {
        NoProxy = [
          "10.0.0.0/8"
        ]
      },
      Environment(
        new Dictionary<string, string> {
          ["https_proxy"] = "http://proxy:3128"
        }
      )
    );
    RoutingProxy proxy = new(
      settings,
      NullLogger<RoutingProxy>.Instance,
      time,
      _ => {
        lookups++;
        return [
          IPAddress.Parse("52.0.0.1")
        ];
      }
    );

    proxy.GetProxy(S_S3_);
    proxy.GetProxy(S_S3_);
    time.Advance(TimeSpan.FromMinutes(2));
    proxy.GetProxy(S_S3_);

    Assert.Equal(2, lookups);
  }

  [Fact]
  public void TunnelAddressesAreTriedFirst() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        NoProxy = [
          "10.20.0.0/16"
        ]
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128"
      }
    );
    IPAddress[] resolved = [
      IPAddress.Parse("2001:db8::1"),
      IPAddress.Parse("52.1.2.3"),
      IPAddress.Parse("10.20.1.5"),
      IPAddress.Parse("10.20.9.9")
    ];

    Assert.Equal(
      [
        "10.20.1.5",
        "10.20.9.9",
        "2001:db8::1",
        "52.1.2.3"
      ],
      proxy.OrderForConnect(resolved, 443).Select(a => a.ToString())
    );
  }

  [Fact]
  public void LoopbackIsAlwaysDirect() {
    RoutingProxy proxy = Create(
      new ProxyOptions(),
      new Dictionary<string, string> {
        ["http_proxy"] = "http://proxy:3128"
      }
    );

    Assert.True(proxy.IsBypassed(new Uri("http://localhost:9000/")));
    Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:9000/")));
    Assert.True(proxy.IsBypassed(new Uri("http://[::1]:9000/")));
  }

  [Fact]
  public void DisabledMeansDirectEvenWithEnvironment() {
    RoutingProxy proxy = Create(
      new ProxyOptions {
        Enabled = false
      },
      new Dictionary<string, string> {
        ["https_proxy"] = "http://proxy:3128"
      }
    );

    Assert.True(proxy.IsBypassed(S_S3_));
  }

  [Fact]
  public void CredentialsComeFromTheUrlAndAreNotInTheProxyUri() {
    RoutingProxy proxy = Create(
      new ProxyOptions(),
      new Dictionary<string, string> {
        ["https_proxy"] = "http://alice:s%40cret@proxy:3128"
      }
    );

    Uri? proxy_uri = proxy.GetProxy(S_S3_);
    NetworkCredential? credential = proxy.Credentials?.GetCredential(
      proxy_uri!,
      "Basic"
    );

    Assert.Equal(new Uri("http://proxy:3128"), proxy_uri);
    Assert.Equal("alice", credential?.UserName);
    Assert.Equal("s@cret", credential?.Password);
  }

  [Theory]
  [InlineData("ftp://proxy:21")]
  [InlineData("http://")]
  public void RejectsBadProxyUrls(string url) {
    Assert.Throws<ConfigurationException>(() => Create(
                                              new ProxyOptions {
                                                Url = url
                                              },
                                              []
                                            )
    );
  }

  private static RoutingProxy Create(ProxyOptions options,
                                     Dictionary<string, string> environment,
                                     Func<string, IPAddress[]>? resolve = null
  ) {
    ProxySettings settings = ProxySettings.Resolve(
      options,
      Environment(environment)
    );
    return new RoutingProxy(
      settings,
      NullLogger<RoutingProxy>.Instance,
      TimeProvider.System,
      resolve ?? (_ => [])
    );
  }

  private static Func<string, string?> Environment(
    Dictionary<string, string> values
  ) {
    return name => values.GetValueOrDefault(name);
  }
}
