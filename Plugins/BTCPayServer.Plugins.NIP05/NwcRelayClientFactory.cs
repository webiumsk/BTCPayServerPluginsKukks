#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NNostr.Client;

namespace BTCPayServer.Plugins.NIP05;

public class NwcRelayClientFactory(IHttpClientFactory httpClientFactory)
{
    public static void ValidateRelay(Uri relay, string? allowedWsOrigins = null)
    {
        if (!relay.IsAbsoluteUri || string.IsNullOrEmpty(relay.Host) ||
            !string.IsNullOrEmpty(relay.UserInfo) || !string.IsNullOrEmpty(relay.Fragment))
            throw new FormatException("NWC relays need an absolute URL without credentials or fragments");
        if (relay.Scheme == "wss") return;
        var origin = relay.GetLeftPart(UriPartial.Authority);
        var allowed = (allowedWsOrigins ?? Environment.GetEnvironmentVariable("BTCPAY_NWC_ALLOWED_WS_ORIGINS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "ws" &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
                uri.AbsolutePath == "/" && uri.GetLeftPart(UriPartial.Authority).Equals(origin, StringComparison.OrdinalIgnoreCase));
        if (relay.Scheme != "ws" || !allowed)
            throw new FormatException("NWC relays require wss or an explicitly authorized operator ws origin");
    }

    public async Task<(INostrClient, IDisposable)> GetClientAndConnect(Uri[] relays, CancellationToken token)
    {
        if (relays.Length is < 1 or > 8) throw new FormatException("NWC requires between one and eight relays");
        foreach (var relay in relays) ValidateRelay(relay);
        NostrClient Create(Uri relay) => new SafeRelayClient(relay, httpClientFactory, token);
        INostrClient client = relays.Length == 1 ? Create(relays[0]) : new SafeCompositeNostrClient(relays, Create);
        try {
            await client.ConnectAndWaitUntilConnected(token, CancellationToken.None);
            return (client, client);
        } catch { client.Dispose(); throw; }
    }

    private sealed class SafeRelayClient(Uri relay, IHttpClientFactory factory, CancellationToken connectionToken) : NostrClient(relay), IDisposable
    {
        // HttpClient remains alive for the connected WebSocket and is disposed with the relay client.
        private HttpClient? _http;
        void IDisposable.Dispose() { base.Dispose(); WebSocket?.Dispose(); _http?.Dispose(); }
        protected override async Task<WebSocket> Connect()
        {
            ValidateRelay(Relay);
            _statusListenerTokenSource?.Cancel();
            _statusListenerTokenSource = new CancellationTokenSource();
            _ = ListenForWebsocketChanges(_statusListenerTokenSource.Token);
            _http?.Dispose();
            _http = factory.CreateClient("nwc.safe");
            var socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token, connectionToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await socket.ConnectAsync(Relay, _http, timeout.Token); return socket; }
            catch { socket.Dispose(); _http.Dispose(); throw; }
        }
    }
}
