#nullable enable
using System;
using System.Linq;
using System.Threading;
using BTCPayServer.Lightning;
using NBitcoin;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.NIP05;

public class NostrWalletConnectLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly NwcRelayClientFactory _nostrClientPool;
    private readonly Microsoft.Extensions.Logging.ILogger<NostrWalletConnectLightningClient> _logger;

    public NostrWalletConnectLightningConnectionStringHandler(NwcRelayClientFactory nostrClientPool,
        Microsoft.Extensions.Logging.ILogger<NostrWalletConnectLightningClient> logger)
    {
        _nostrClientPool = nostrClientPool;
        _logger = logger;
    }
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        
        
        if (!connectionString.StartsWith(NIP47.UriScheme, StringComparison.OrdinalIgnoreCase) && !connectionString.StartsWith("type=nwc;key="))
        {
            error = null;
            return null;
        }

        connectionString = connectionString.Replace("type=nwc;key=", "");
        if (!connectionString.StartsWith(NIP47.UriScheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Invalid nostr wallet connect uri (must start with {NIP47.UriScheme})";
            return null;
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort ||
            (uri.AbsolutePath != "" && uri.AbsolutePath != "/"))
        {
            error = "Invalid nostr wallet connect uri";
            return null;
        }

        try {
            var parsed = NIP47.ParseUri(uri);
            if (parsed.relays.Length is < 1 or > 8) throw new FormatException();
            foreach (var relay in parsed.relays) NwcRelayClientFactory.ValidateRelay(relay);
        } catch {
            error = "Invalid NWC keys or relay destinations";
            return null;
        }
        error = null;
		return new NostrWalletConnectLightningClient(_nostrClientPool, uri, network, _logger);
	}
}