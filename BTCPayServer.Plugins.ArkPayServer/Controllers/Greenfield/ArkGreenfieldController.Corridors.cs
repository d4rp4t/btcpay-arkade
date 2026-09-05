using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Hosting;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
    /// <summary>
    /// Report the Arkade swap solver this store's Lightning corridors run through.
    /// </summary>
    /// <remarks>
    /// This replaces the <c>boltz-limits</c> endpoint and does not carry limits, because there are
    /// none to publish: an Arkade solver quotes its terms per request over RFQ, so the only way to
    /// state a minimum, a maximum or a fee would be to open a negotiation on behalf of a caller that
    /// asked a read-only question. What a caller can act on is whether a corridor exists at all and
    /// in which directions, which is what this returns.
    /// </remarks>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/lightning-solver")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetLightningSolver(string storeId)
    {
        var (_, error) = GetStoreConfig();
        if (error != null) return error;

        if (!arkadeSolver.IsConfigured)
            return this.CreateAPIError(404, "solver-not-configured",
                "No Arkade swap solver can be reached: this network publishes no solver registry and "
                + "none is named in ark.json ('solver-relay' and 'solver-pubkey').");

        return Ok(new ArkLightningSolverData
        {
            RelayUri = arkadeSolver.RelayUri,
            SolverPubkey = arkadeSolver.SolverPubkey
        });
    }
}
