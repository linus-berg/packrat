using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;

namespace PackRat.Networking;

/// <summary>
///   Builds the AWS SDK's HttpClients the same way the SDK does, but routed
///   through <see cref="RoutingProxy" /> and tuned for flaky paths (IPsec
///   tunnels, proxies): pooled connections are recycled quickly, because
///   rekeys, tunnel flaps and idle timeouts silently kill long-lived TCP
///   connections, and stalled connections are detected within minutes.
/// </summary>
public sealed class RoutingHttpClientFactory : HttpClientFactory {
  private const int C_IPPROTO_TCP_ = 6;
  private const int C_TCP_USER_TIMEOUT_ = 18;

  /// <summary>
  ///   How long sent data may stay unacknowledged before the connection is
  ///   dropped (Linux default: ~15 minutes of retransmits).
  /// </summary>
  private static readonly TimeSpan S_USER_TIMEOUT_ = TimeSpan.FromMinutes(2);

  private readonly RoutingProxy proxy_;

  public RoutingHttpClientFactory(RoutingProxy proxy) {
    proxy_ = proxy;
  }

  public override HttpClient CreateHttpClient(IClientConfig client_config) {
    SocketsHttpHandler handler = new() {
      EnableMultipleHttp2Connections = true,
      AllowAutoRedirect = client_config.AllowAutoRedirect,
      AutomaticDecompression = DecompressionMethods.None,
      UseProxy = true,
      Proxy = proxy_,
      PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
      PooledConnectionLifetime = TimeSpan.FromMinutes(5),
      ConnectCallback = ConnectAsync
    };

    if (client_config.ConnectTimeout is TimeSpan connect_timeout) {
      handler.ConnectTimeout = connect_timeout;
    }

    if (client_config.MaxConnectionsPerServer is int max_connections) {
      handler.MaxConnectionsPerServer = max_connections;
    }

    HttpClient client = new(handler);
    if (client_config.Timeout is TimeSpan timeout) {
      client.Timeout = timeout;
    }

    return client;
  }

  /// <summary>
  ///   Same as the handler's default connect, except that addresses inside
  ///   IP/CIDR no_proxy entries are tried first and keepalive plus
  ///   TCP_USER_TIMEOUT are set. Used for direct connections and for the
  ///   connection to the proxy itself.
  /// </summary>
  private async ValueTask<Stream> ConnectAsync(
    SocketsHttpConnectionContext context,
    CancellationToken cancellation_token
  ) {
    DnsEndPoint endpoint = context.DnsEndPoint;
    IPAddress[] addresses = IPAddress.TryParse(
                              endpoint.Host,
                              out IPAddress? literal
                            )
      ? [
        literal
      ]
      : await Dns.GetHostAddressesAsync(endpoint.Host, cancellation_token);
    addresses = proxy_.OrderForConnect(addresses, endpoint.Port);

    Socket socket = new(SocketType.Stream, ProtocolType.Tcp) {
      NoDelay = true
    };
    try {
      ConfigureLiveness(socket);
      await socket.ConnectAsync(addresses, endpoint.Port, cancellation_token);
      return new NetworkStream(socket, true);
    } catch {
      socket.Dispose();
      throw;
    }
  }

  private static void ConfigureLiveness(Socket socket) {
    try {
      socket.SetSocketOption(
        SocketOptionLevel.Socket,
        SocketOptionName.KeepAlive,
        true
      );
      socket.SetSocketOption(
        SocketOptionLevel.Tcp,
        SocketOptionName.TcpKeepAliveTime,
        30
      );
      socket.SetSocketOption(
        SocketOptionLevel.Tcp,
        SocketOptionName.TcpKeepAliveInterval,
        10
      );
      socket.SetSocketOption(
        SocketOptionLevel.Tcp,
        SocketOptionName.TcpKeepAliveRetryCount,
        3
      );
      if (OperatingSystem.IsLinux()) {
        socket.SetRawSocketOption(
          C_IPPROTO_TCP_,
          C_TCP_USER_TIMEOUT_,
          BitConverter.GetBytes((uint)S_USER_TIMEOUT_.TotalMilliseconds)
        );
      }
    } catch (SocketException) {
      // Best effort; the request still works without these.
    }
  }
}
