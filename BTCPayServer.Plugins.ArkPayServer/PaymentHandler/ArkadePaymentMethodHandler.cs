using BTCPayServer.Data;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.Onchain;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NArk.Core;
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
                await contractService.ImportContract(
                    arkadePaymentMethodConfig.WalletId,
                    boardingContract,
                    metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
                    cancellationToken: CancellationToken.None);

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

                // Trigger sync so NBXplorer starts tracking this boarding address immediately
                _ = Task.Run(() => boardingUtxoSyncService.SyncAsync(CancellationToken.None));

                var swap = arkadePaymentMethodConfig.OnchainSwapEnabled
                    ? await NegotiateOnchainSwapAsync(
                        arkadePaymentMethodConfig.WalletId, amountSats, boardingAddress)
                    : null;

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
    /// </remarks>
    private async Task<PendingOnchainReceive?> NegotiateOnchainSwapAsync(
        string walletId, long amountSats, BitcoinAddress refundDestination)
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
                    cancellationToken: timeout.Token),
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
