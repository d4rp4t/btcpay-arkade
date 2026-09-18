using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

/// <summary>
/// Turns each wallet's configured destination into a settlement rule.
/// </summary>
/// <remarks>
/// Only an explicit destination produces a rule. Consolidating a wallet onto its own address is
/// deliberately left to <see cref="Policies.SingleKeyConsolidationSweepPolicy"/>: a settlement rule
/// is stated in balance, and a rule that settles a wallet to itself is satisfied by its own output,
/// so it re-fires on the VTXO it just created and spins. Skipping the coins that already sit at the
/// target is a coin-level test the config API cannot express.
/// <para>
/// A destination flagged <see cref="DestinationSafety.PendingConfirmationMetadataKey"/> is treated
/// as absent — the operator rotated its signer and the merchant has not re-confirmed the address,
/// so paying it could pay somewhere the store no longer controls.
/// </para>
/// </remarks>
public class WalletDestinationSettlementConfigProvider(IWalletStorage walletStorage)
    : ISettlementConfigProvider
{
    /// <inheritdoc />
    public async Task<IReadOnlyCollection<SettlementConfig>> GetConfigs(
        string? walletId = null,
        CancellationToken cancellationToken = default)
    {
        var wallets = walletId is null
            ? (IReadOnlyCollection<ArkWalletInfo>)await walletStorage.LoadAllWallets(cancellationToken)
            : await walletStorage.GetWalletsByIds([walletId], cancellationToken);

        var configs = new List<SettlementConfig>(wallets.Count);
        foreach (var wallet in wallets)
        {
            if (ResolveDestination(wallet) is not { } destination)
                continue;

            configs.Add(new SettlementConfig(wallet.Id, destination, Threshold: 0));
        }

        return configs;
    }

    private static SettlementDestination? ResolveDestination(ArkWalletInfo wallet)
        => !string.IsNullOrEmpty(wallet.Destination) && !IsPendingConfirmation(wallet)
            ? SettlementDestination.Ark(wallet.Destination)
            : null;

    private static bool IsPendingConfirmation(ArkWalletInfo wallet)
        => wallet.Metadata?.ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) == true;
}
