using System.Net;
using System.Net.Sockets;
using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.NIP05;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

public class ConnectionSecurityTests
{
    [Theory]
    [InlineData("ws://relay.example")]
    [InlineData("https://relay.example")]
    [InlineData("wss://user:password@relay.example")]
    [InlineData("wss://relay.example/#fragment")]
    public void Rejects_unsafe_relay_syntax(string value)
        => Assert.Throws<FormatException>(() => NwcRelayClientFactory.ValidateRelay(new Uri(value)));

    [Fact]
    public void Operator_ws_exception_requires_exact_origin_and_port()
    {
        NwcRelayClientFactory.ValidateRelay(new Uri("ws://relay.lan:8888/nwc"), "ws://relay.lan:8888");
        Assert.Throws<FormatException>(() => NwcRelayClientFactory.ValidateRelay(new Uri("ws://relay.lan:8889/nwc"), "ws://relay.lan:8888"));
    }

    [Fact]
    public async Task Relay_websocket_uses_host_ssrf_transport_before_opening_a_socket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var services = new ServiceCollection();
        services.AddSingleton(new BTCPayServerOptions());
        services.AddHttpClient("nwc.safe").UseSSRFProtection()
            .ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).AllowAutoRedirect = false);
        using var provider = services.BuildServiceProvider();
        var factory = new NwcRelayClientFactory(provider.GetRequiredService<IHttpClientFactory>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(() => factory.GetClientAndConnect(
            [new Uri($"wss://127.0.0.1:{port}")], timeout.Token));
        Assert.False(listener.Pending());
    }

    [Fact]
    public void Invalid_nwc_error_does_not_echo_secret()
    {
        var handler = new NostrWalletConnectLightningConnectionStringHandler(null!, NullLogger<NostrWalletConnectLightningClient>.Instance);
        Assert.Null(handler.Create("type=nwc;key=nostr+walletconnect://invalid?secret=sensitive&relay=ws%3A%2F%2Fprivate", Network.Main, out var error));
        Assert.DoesNotContain("sensitive", error!);
    }
}
