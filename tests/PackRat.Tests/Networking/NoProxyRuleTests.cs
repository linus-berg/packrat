using System.Net;
using PackRat.Networking;

namespace PackRat.Tests.Networking;

public sealed class NoProxyRuleTests {
  [Theory]
  [InlineData("example.com", "example.com", true)]
  [InlineData("example.com", "s3.example.com", true)]
  [InlineData("example.com", "a.b.example.com", true)]
  [InlineData("example.com", "notexample.com", false)]
  [InlineData(".example.com", "example.com", true)]
  [InlineData(".example.com", "s3.example.com", true)]
  [InlineData("*.example.com", "s3.example.com", true)]
  [InlineData("*.example.com", "example.com", true)]
  [InlineData("EXAMPLE.com.", "s3.Example.COM", true)]
  [InlineData("*", "anything.at.all", true)]
  public void MatchesDomains(string entry, string host, bool expected) {
    Assert.True(NoProxyRule.TryParse(entry, out NoProxyRule? rule));
    string normalized = NoProxyRule.NormalizeHost(host);

    Assert.Equal(expected, rule.MatchesHost(normalized, null, 443));
  }

  [Theory]
  [InlineData("10.0.0.0/8", "10.20.30.40", true)]
  [InlineData("10.0.0.0/8", "11.0.0.1", false)]
  [InlineData("192.168.1.0/24", "192.168.1.255", true)]
  [InlineData("192.168.1.0/24", "192.168.2.1", false)]
  [InlineData("172.16.0.0/12", "172.31.255.255", true)]
  [InlineData("172.16.0.0/12", "172.32.0.1", false)]
  [InlineData("10.1.2.3", "10.1.2.3", true)]
  [InlineData("10.1.2.3", "10.1.2.4", false)]
  [InlineData("0.0.0.0/0", "8.8.8.8", true)]
  [InlineData("fd00::/8", "fd12:3456::1", true)]
  [InlineData("fd00::/8", "fe80::1", false)]
  [InlineData("[fd00::1]", "fd00::1", true)]
  [InlineData("10.0.0.0/8", "::ffff:10.1.1.1", true)]
  public void MatchesAddressesAndRanges(string entry,
                                        string address,
                                        bool expected
  ) {
    Assert.True(NoProxyRule.TryParse(entry, out NoProxyRule? rule));
    IPAddress parsed = NoProxyRule.Normalize(IPAddress.Parse(address));

    Assert.Equal(
      expected,
      rule.MatchesHost(parsed.ToString(), parsed, 443)
    );
    Assert.Equal(expected, rule.MatchesResolvedAddress(parsed, 443));
  }

  [Fact]
  public void PortRestrictsTheEntry() {
    Assert.True(NoProxyRule.TryParse("minio.lan:9000", out NoProxyRule? rule));

    Assert.True(rule.MatchesHost("minio.lan", null, 9000));
    Assert.False(rule.MatchesHost("minio.lan", null, 443));
  }

  [Fact]
  public void BracketedIpv6WithPort() {
    Assert.True(NoProxyRule.TryParse("[fd00::5]:9000", out NoProxyRule? rule));
    IPAddress address = IPAddress.Parse("fd00::5");

    Assert.True(rule.MatchesHost("fd00::5", address, 9000));
    Assert.False(rule.MatchesHost("fd00::5", address, 443));
  }

  [Fact]
  public void DomainEntriesNeverMatchResolvedAddresses() {
    Assert.True(NoProxyRule.TryParse("example.com", out NoProxyRule? rule));

    Assert.False(rule.MatchesResolvedAddress(IPAddress.Loopback, 443));
  }

  [Fact]
  public void ShorthandNumbersAreNotTreatedAsAddresses() {
    // IPAddress.Parse("10.1") would give 10.0.0.1.
    Assert.True(NoProxyRule.TryParse("10.1", out NoProxyRule? rule));

    Assert.False(rule.IsNetwork);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("10.0.0.0/33")]
  [InlineData("10.0.0.0/abc")]
  [InlineData("host:99999")]
  [InlineData("host:port")]
  [InlineData("[fd00::1")]
  [InlineData("foo*bar")]
  public void RejectsInvalidEntries(string entry) {
    Assert.False(NoProxyRule.TryParse(entry, out _));
  }

  [Fact]
  public void ListSplitsCommaSeparatedEnvironmentValues() {
    NoProxyList list = NoProxyList.Parse(
      [
        "localhost, .corp.example.com,10.0.0.0/8",
        "10.0.0.0/8",
        "bad/entry/",
        null
      ]
    );

    Assert.Equal(
      [
        "localhost",
        ".corp.example.com",
        "10.0.0.0/8"
      ],
      list.Rules.Select(r => r.Text)
    );
    Assert.Equal(["bad/entry/"], list.Invalid);
    Assert.True(list.HasNetworkRules);
  }
}
