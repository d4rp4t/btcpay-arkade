using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Sweeper;
using NArk.Core.Transport;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Consolidates a SingleKey wallet that has no usable destination onto its own default address.
/// </summary>
/// <remarks>
/// Every receive address such a wallet hands out is a hash-locked contract with its own preimage,
/// so without this the store's balance sits scattered across one address per invoice.
/// <para>
/// This stays a sweep policy rather than a settlement rule because the loop-breaker is a property
/// of the coins, not the balance: coins already at the default address are skipped, and a
/// settlement rule — stated in balance, satisfied by its own output — has no way to say that, so it
/// would re-fire on the VTXO it just created.
/// </para>
/// <para>
/// A wallet with a destination is left to the settlement service. A destination awaiting
/// re-confirmation after a signer rotation counts as absent, so the balance keeps consolidating
/// here rather than paying an address the store may no longer control.
/// </para>
/// </remarks>
public class SingleKeyConsolidationSweepPolicy(IWalletStorage walletStorage, IClientTransport clientTransport)
    : ISweepPolicy
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ArkCoin> SweepAsync(
        IEnumerable<ArkCoin> coins,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // VHTLCs are SwapSweepPolicy's job; these are the two contract types a payment lands on.
        var spendableCoins = coins
            .Where(c => c.Contract is ArkPaymentContract or HashLockedArkPaymentContract)
            .ToArray();

        if (spendableCoins.Length == 0)
            yield break;

        foreach (var group in spendableCoins.GroupBy(c => c.WalletIdentifier))
        {
            var wallet = await walletStorage.GetWalletById(group.Key, cancellationToken);
            if (wallet is null || wallet.WalletType != WalletType.SingleKey)
                continue;

            if (!string.IsNullOrEmpty(wallet.Destination)
                && wallet.Metadata?.ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) != true)
                continue;

            // The address wallet setup created, and the one SendToSelf re-derives: for a single-key
            // descriptor it is fixed, so it is both the consolidation target and the stop condition.
            var info = await clientTransport.GetServerInfoAsync(cancellationToken);
            var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, info.Network);
            var defaultScript = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, descriptor)
                .GetArkAddress().ScriptPubKey;

            foreach (var coin in group.Where(c => c.TxOut.ScriptPubKey != defaultScript))
                yield return coin;
        }
    }
}
