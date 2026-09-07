using BTCPayServer.Data;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Onchain;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NArk.Core;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    IContractService contractService,
    IClientTransport clientTransport,
    BoardingUtxoSyncService boardingUtxoSyncService,
    IWalletStorage walletStorage,
    ILogger<ArkadePaymentMethodHandler> logger,
    ArkadeIntentsService? intents = null,
    ArkadeSolverService? solver = null
) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        ArkServerInfo serverInfo;
        try
        {
            serverInfo = await clientTransport.GetServerInfoAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        var store = context.Store;

        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not ArkadePaymentMethodConfig
            arkadePaymentMethodConfig)
        {
            throw new PaymentMethodUnavailableException("Arkade payment method not configured");
        }

        if (!arkadePaymentMethodConfig.AllowSubDustAmounts && Money.Coins(context.Prompt.Calculate().Due) < serverInfo.Dust)
        {
            throw new PaymentMethodUnavailableException("Amount too small");
        }

        var contract = await contractService.DeriveContract(
            arkadePaymentMethodConfig.WalletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);
        var details = new ArkadePromptDetails(arkadePaymentMethodConfig.WalletId, contract);
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());

        // Derive boarding address when: boarding enabled, no onchain BTC configured, HD wallet, amount above threshold.
        // SingleKey (nsec) wallets are excluded because they derive the same boarding address for every invoice,
        // causing duplicate AddressInvoice conflicts.
        // Reuse the same signing descriptor from the Ark payment contract to avoid consuming an extra HD index.
        var hasOnchain = context.InvoiceEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC")) is not null;
        var wallet = await walletStorage.GetWalletById(arkadePaymentMethodConfig.WalletId);
        var amountSats = Money.Coins(context.Prompt.Calculate().Due).Satoshi;
        // Derived whenever EITHER onchain path is on, because the swap needs it too — not as a
        // method it offers, but as where its L1 refund goes. Sending that refund to an address
        // BTCPay never associated with this invoice would leave the merchant paid and the invoice
        // open, so the sink has to be an address registered here, at prompt time.
        var wantsBoarding = arkadePaymentMethodConfig.BoardingEnabled
            || arkadePaymentMethodConfig.OnchainSwapEnabled;
        if (wantsBoarding &&
            !hasOnchain && wallet?.WalletType == WalletType.HD &&
            amountSats >= arkadePaymentMethodConfig.MinBoardingAmountSats)
        {
                // Construct boarding contract from same user descriptor — no extra DeriveContract call
                var userDescriptor = contract switch
                {
                    ArkPaymentContract pc => pc.User,
                    ArkDelegateContract dc => dc.User,
                    _ => throw new PaymentMethodUnavailableException("Unsupported contract type for boarding")
                };
                var boardingContract = new ArkBoardingContract(
                    serverInfo.SignerKey, serverInfo.BoardingExit, userDescriptor);

                var network = btcPayServerEnvironment.NetworkType == ChainName.Mainnet
                    ? Network.Main
                    : btcPayServerEnvironment.NetworkType == ChainName.Testnet
                        ? Network.TestNet
                        : Network.RegTest;
                var boardingAddress = boardingContract.GetOnchainAddress(network);

                // Tracked whichever path is taken: on the swap path this is where the refund lands,
                // and BTCPay credits a payment to an invoice by the destinations registered here.
                context.TrackedDestinations.Add(boardingAddress.ToString());
                context.TrackedDestinations.Add(boardingContract.GetScriptPubKey().ToHex());

                var swap = arkadePaymentMethodConfig.OnchainSwapEnabled
                    ? await NegotiateOnchainSwapAsync(
                        arkadePaymentMethodConfig.WalletId, amountSats, boardingAddress, contract)
                    : null;

                // Imported after the negotiation, because the Source tag depends on which role this
                // contract ends up in, and the two roles have opposite lifetimes.
                //
                // As an offered method it belongs to the invoice: `ToggleArkadeContract` deactivates
                // every contract tagged `invoice:{id}` once the invoice stops being New, which is
                // right — nobody should be paying it after that.
                //
                // As a swap's refund sink it has to OUTLIVE the invoice. The refund cannot be pushed
                // until the L1 locktime, hours later and long after the invoice expired, and a
                // contract deactivated by then is a script nobody is watching when the money finally
                // lands on it. That is a silent loss, so the tag is deliberately one the invoice
                // sweep does not match.
                // Only when it will actually be used: as the swap's refund sink, or as a method this
                // store offers. With boarding switched off and a swap that could not be negotiated,
                // it is neither — importing it anyway would leave a contract nothing pays and the
                // sync service still watches.
                var boardingSource = swap is not null
                    ? $"swap-refund:{swap.RfqId}"
                    : arkadePaymentMethodConfig.BoardingEnabled
                        ? $"invoice:{context.InvoiceEntity.Id}"
                        : null;

                if (boardingSource is not null)
                {
                    await contractService.ImportContract(
                        arkadePaymentMethodConfig.WalletId,
                        boardingContract,
                        metadata: new Dictionary<string, string> { ["Source"] = boardingSource },
                        cancellationToken: CancellationToken.None);

                    // Trigger sync so NBXplorer starts tracking this boarding address immediately
                    _ = Task.Run(() => boardingUtxoSyncService.SyncAsync(CancellationToken.None));
                }

                if (swap is not null)
                {
                    // The swap replaces boarding rather than joining it. Both are onchain addresses
                    // and they want different amounts — offering the pair invites a payer to split a
                    // payment between them, which funds neither.
                    details = details with
                    {
                        SwapHtlcAddress = swap.HtlcAddress,
                        SwapFundAmountSats = swap.FundAmountSats,
                        SwapId = swap.RfqId,
                    };
                    // Registered so the invoice can be found from the address a payer was shown —
                    // a support question, a webhook, a manual reconciliation. It credits NOTHING on
                    // its own: crediting runs off VTXO events, and nothing imports the L1 HTLC as a
                    // contract, so no event ever names this script. That is the intended shape
                    // rather than an omission. The swap is not the merchant's money while it sits in
                    // the HTLC — it becomes theirs when the claim lands on the prompt's own address,
                    // which is the destination registered above, and that is the one credit.
                    context.TrackedDestinations.Add(swap.HtlcAddress);
                }
                else if (arkadePaymentMethodConfig.BoardingEnabled)
                {
                    details = details with
                    {
                        BoardingAddress = boardingAddress.ToString(),
                        BoardingContractString = boardingContract.ToString(),
                    };
                }
        }

        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    /// <summary>
    /// Negotiate the fast onchain path, or return <c>null</c> to fall back to boarding.
    /// </summary>
    /// <param name="walletId">The store's wallet.</param>
    /// <param name="amountSats">What the invoice is due.</param>
    /// <param name="refundDestination">
    /// Where the L1 refund goes if the swap never settles — this invoice's own boarding address, so
    /// a failed swap degrades into the slow path rather than into a reconciliation problem.
    /// </param>
    /// <param name="payoutContract">
    /// The contract this invoice already derived, reused as the swap's payout. Load-bearing — see
    /// the remarks before changing it.
    /// </param>
    /// <returns>The negotiated on-board, or <c>null</c> when this path is not available.</returns>
    /// <remarks>
    /// <para>
    /// Every failure here returns <c>null</c> rather than throwing. A solver that is unlisted, slow,
    /// unreachable or simply unwilling to quote is a reason to offer the slower path, not a reason
    /// the merchant cannot be paid — and this runs while a customer is waiting for a checkout page.
    /// </para>
    /// <para>
    /// The corridor is asked for by name: a solver listed for Lightning is not thereby listed for
    /// onchain.
    /// </para>
    /// <para>
    /// Exact-OUT, unlike the Lightning leg beside it, and the difference is not a preference. What
    /// credits an invoice here is the VTXO that lands, so pinning the L1 side instead would have the
    /// solver's fee come out of what the merchant receives and leave every such invoice underpaid by
    /// it. Lightning credits the BOLT11 amount rather than what lands, so pinning the payer's side
    /// there is right for the same reason it is wrong here. Exact-out also cannot be refused for
    /// <c>fee_consumes_swap</c> — the payer's figure is solved up from the payout, so the fee has
    /// nothing to eat.
    /// </para>
    /// <para>
    /// The payout is this invoice's own contract, and here that is a correctness requirement rather
    /// than the tidiness it is at the SDK layer. What credits an invoice is
    /// <see cref="ArkContractInvoiceListener"/> matching an arriving VTXO's address against
    /// <c>TrackedDestinations</c>, and the only Arkade address registered there is the prompt's own.
    /// A freshly derived payout is registered nowhere: the claim would succeed, the sats would be in
    /// the wallet, and the invoice would sit unpaid with nothing in the logs to say why. So passing
    /// the prompt's contract is what connects the corridor to the invoice at all.
    /// </para>
    /// <para>
    /// It also happens to cost no HD index, which is worth keeping: an HD wallet is restored by
    /// scanning until `GapLimit` consecutive indices come back unused, and an invoice that is never
    /// paid leaves whatever it derived behind, so deriving twice per invoice reaches that limit at
    /// twice the rate and what lies past it a seed restore does not find. The cost of sharing is
    /// that one key appears in three contracts for one payment, which links them; they are one
    /// payment, so the link exists regardless.
    /// </para>
    /// </remarks>
    private async Task<PendingOnchainReceive?> NegotiateOnchainSwapAsync(
        string walletId, long amountSats, BitcoinAddress refundDestination, ArkContract payoutContract)
    {
        if (intents is null || solver is null) return null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await solver.HasSolverForAsync(
                    ArkadeSolverSelector.OnchainCorridor, timeout.Token))
            {
                return null;
            }

            var covclaimd = await solver.ResolveClaimRecipientAsync(timeout.Token);
            return await solver.WithTransportAsync(
                amountSats, ArkadeSolverSelector.OnchainCorridor,
                transport => intents.ReceiveFromOnchainAsync(
                    walletId, amountSats, transport, covclaimd, refundDestination,
                    amountSide: RfqAmountSide.To,
                    payoutContract: payoutContract, cancellationToken: timeout.Token),
                timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogInformation(
                e, "No onchain swap for this invoice; offering the boarding path instead");
            return null;
        }
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        return Task.CompletedTask;
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public ArkadePromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadePromptDetails>(Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadePaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentMethodHandler)}");
    }

    public ArkadePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentData)}");
    }
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}
