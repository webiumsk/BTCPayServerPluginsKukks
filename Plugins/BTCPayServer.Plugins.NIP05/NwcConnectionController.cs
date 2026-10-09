#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.NIP05;

/// <summary>Typed NWC setup using the caller's store authorization. No privileged credentials,
/// arbitrary connection strings, unsafe network flag, or secret-bearing response.</summary>
[ApiController]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class NwcConnectionController(StoreRepository stores, InvoiceRepository invoices,
    NostrWalletConnectLightningConnectionStringHandler handler, PaymentMethodHandlerDictionary methods,
    BTCPayNetworkProvider networks) : ControllerBase
{
    public sealed record ConnectionRequest(string Uri);

    [HttpPut("~/api/v1/stores/{storeId}/nwc/connection")]
    public async Task<IActionResult> Connect(string storeId, [FromBody] ConnectionRequest request)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null || store.Id != storeId) return NotFound();
        if (request.Uri is null || request.Uri.Length > 4096 ||
            !request.Uri.StartsWith("nostr+walletconnect://", StringComparison.OrdinalIgnoreCase) || request.Uri.Contains(';'))
            return this.CreateAPIError(422, "invalid-nwc", "A valid NWC URI is required");
        var id = new PaymentMethodId("BTC-LN");
        var connection = "type=nwc;key=" + request.Uri;
        var previous = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(id, methods);
        if (previous?.ConnectionString == connection && !store.GetStoreBlob().GetExcludedPaymentMethods().Match(id))
            return Ok(new { enabled = true });
        if (previous?.ConnectionString != connection)
        {
            var active = await invoices.GetInvoices(new InvoiceQuery { StoreId = new[] { store.Id }, IncludeArchived = true,
                Status = new[] { "New", "Processing", "Expired", "Invalid" } });
            if (active.Any(i => i.Status == BTCPayServer.Client.Models.InvoiceStatus.Processing || i.MonitoringExpiration > DateTimeOffset.UtcNow))
                return this.CreateAPIError(409, "wallet-invoices-pending", "Wait until outstanding invoices finish monitoring before changing the wallet");
        }
        var client = handler.Create(connection, networks.BTC.NBitcoinNetwork, out var error);
        if (client is not IExtendedLightningClient typed || error is not null)
            return this.CreateAPIError(422, "invalid-nwc", "Invalid NWC keys or relay destinations");
        try {
            var validation = await typed.Validate();
            if (validation != System.ComponentModel.DataAnnotations.ValidationResult.Success)
                return this.CreateAPIError(422, "invalid-nwc", "NWC wallet validation failed");
        } catch {
            return this.CreateAPIError(422, "nwc-unreachable", "NWC wallet or relay is blocked, unreachable, or did not respond");
        }
        store.SetPaymentMethodConfig(id, JObject.FromObject(new LightningPaymentMethodConfig {
            ConnectionString = connection, AllowUnsafeConnection = false }));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(id, false);
        store.SetStoreBlob(blob);
        await stores.UpdateStore(store);
        return Ok(new { enabled = true });
    }
}
