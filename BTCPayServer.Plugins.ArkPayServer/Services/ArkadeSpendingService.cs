using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpendingService(
    ISpendingService arkadeSpender,
    IClientTransport clientTransport,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
{
    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Arkade address, or a BIP21 URI carrying one
    /// (as the <c>ark</c> query parameter or as the URI host).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Arkade transaction ID.</returns>
    /// <exception cref="MalformedPaymentDestination">
    /// The destination is not an Arkade address — including a BOLT11 invoice, which this wallet has
    /// no rail to pay since Arkade Lightning was removed.
    /// </exception>
    public Task<string?> Spend(StoreData store, string destination, CancellationToken cancellationToken)
        => Spend(store, destination, amountSats: null, inputOutpoints: null, cancellationToken);

    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination, with explicit amount and/or coin selection.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Arkade address, or a BIP21 URI carrying one
    /// (as the <c>ark</c> query parameter or as the URI host).
    /// </param>
    /// <param name="amountSats">
    /// Optional amount in satoshis. When provided, overrides any amount embedded in the destination
    /// (e.g. BIP21 <c>amount</c> query parameter). Required for bare Arkade addresses unless the address
    /// is embedded inside a BIP21 URI with an amount.
    /// </param>
    /// <param name="inputOutpoints">
    /// Optional list of VTXO outpoints (in <c>txid:vout</c> form) to spend. When provided, the wallet's
    /// automatic coin selection is bypassed and only the specified coins are used as inputs.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Arkade transaction ID.</returns>
    /// <exception cref="MalformedPaymentDestination">
    /// The destination is not an Arkade address — including a BOLT11 invoice, which this wallet has
    /// no rail to pay since Arkade Lightning was removed.
    /// </exception>
    public async Task<string?> Spend(
        StoreData store,
        string destination,
        long? amountSats,
        IReadOnlyList<string>? inputOutpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        destination = (destination ?? throw new ArgumentNullException(nameof(destination))).Trim();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            throw new IncompleteArkadeSetupException("arkade wallet setup was not done!");

        if (!config.GeneratedByStore)
            throw new IncompleteArkadeSetupException("Wallet does not belong to the current store.");

        if (amountSats is < 0)
            throw new MalformedPaymentDestination("Amount must be non-negative.");

        var hasExplicitInputs = inputOutpoints is { Count: > 0 };

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Recognised only to say why it cannot be paid — otherwise it falls through and the
        // merchant is told the destination is malformed, which it is not.
        if (BOLT11PaymentRequest.TryParse(
                destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase),
                out _, terms.Network))
        {
            throw new MalformedPaymentDestination(
                "Lightning destinations are not supported: the Arkade wallet cannot pay a BOLT11 invoice.");
        }

        // Resolve destination + amount for Ark-targeted payments.
        var (arkAddress, parsedAmount) = TryResolveArkDestination(destination);
        if (arkAddress is null)
            throw new MalformedPaymentDestination();

        // Amount precedence: explicit amountSats > amount encoded in destination.
        Money? amount = amountSats.HasValue ? Money.Satoshis(amountSats.Value) : parsedAmount;
        if (amount is null || amount == Money.Zero)
            throw new MalformedPaymentDestination(
                "Amount is required: provide amountSats, or include an amount in the BIP21 URI.");

        var output = new ArkTxOut(ArkTxOutType.Vtxo, amount, arkAddress);

        try
        {
            uint256 txId;
            if (hasExplicitInputs)
            {
                var selectedCoins = await ResolveCoinsForOutpoints(config.WalletId, inputOutpoints!, cancellationToken);
                txId = await arkadeSpender.Spend(config.WalletId, selectedCoins, [output], cancellationToken);
            }
            else
            {
                txId = await arkadeSpender.Spend(config.WalletId, [output], cancellationToken);
            }

            // Poll for VTXO updates on active contracts — constrain to the last few
            // minutes so wallets with large historical VTXO counts don't re-fetch everything.
            var activeContracts = await contractStorage.GetContracts(
                walletIds: [config.WalletId], isActive: true, cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(
                activeContracts.Select(c => c.Script).ToHashSet(),
                after: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                cancellationToken);

            return txId.ToString();
        }
        catch (MalformedPaymentDestination)
        {
            throw;
        }
        catch (Exception e) when (e is not ArkadePaymentFailedException && e is not OperationCanceledException)
        {
            throw new ArkadePaymentFailedException(e.Message);
        }
    }

    /// <summary>
    /// Try to parse <paramref name="destination"/> as a bare Ark address or a BIP21 URI carrying one,
    /// returning the resolved address and any embedded amount.
    /// </summary>
    private static (ArkAddress? Address, Money? Amount) TryResolveArkDestination(string destination)
    {
        // Bare Ark address (no URI scheme)
        if (ArkAddress.TryParse(destination, out var bareAddress) && bareAddress is not null)
        {
            return (bareAddress, null);
        }

        // BIP21 URI: bitcoin:<host>?ark=<addr>&amount=<btc>
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            // uri.Host is empty for bitcoin: URIs, so we must parse the host portion ourselves.
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            ArkAddress? address = null;
            if (ArkAddress.TryParse(host, out var hostAddress) && hostAddress is not null)
            {
                address = hostAddress;
            }
            else if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsAddress) && qsAddress is not null)
            {
                address = qsAddress;
            }

            if (address is null)
                return (null, null);

            Money? amount = null;
            if (qs["amount"] is { Length: > 0 } amountStr &&
                decimal.TryParse(amountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var amountBtc) &&
                amountBtc > 0)
            {
                amount = Money.Coins(amountBtc);
            }

            return (address, amount);
        }

        return (null, null);
    }

    /// <summary>
    /// Resolve the provided <c>txid:vout</c> outpoint strings to <see cref="ArkCoin"/>s from the wallet's
    /// available coin set. Throws if any outpoint cannot be found or is malformed.
    /// </summary>
    private async Task<ArkCoin[]> ResolveCoinsForOutpoints(
        string walletId,
        IReadOnlyList<string> outpoints,
        CancellationToken cancellationToken)
    {
        var available = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);
        var byOutpoint = available.ToDictionary(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}");
        var resolved = new List<ArkCoin>(outpoints.Count);
        var missing = new List<string>();

        foreach (var raw in outpoints)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            var parts = trimmed.Split(':');
            if (parts.Length != 2 || !uint256.TryParse(parts[0], out _) || !uint.TryParse(parts[1], out _))
            {
                throw new MalformedPaymentDestination(
                    $"Input outpoint '{trimmed}' is not a valid txid:vout string.");
            }

            if (byOutpoint.TryGetValue(trimmed, out var coin))
            {
                resolved.Add(coin);
            }
            else
            {
                missing.Add(trimmed);
            }
        }

        if (resolved.Count == 0)
            throw new ArkadePaymentFailedException(
                "None of the provided input outpoints match a spendable coin in this wallet.");

        if (missing.Count > 0)
            throw new ArkadePaymentFailedException(
                $"The following input outpoints are not spendable from this wallet: {string.Join(", ", missing)}.");

        return resolved.ToArray();
    }

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

}
