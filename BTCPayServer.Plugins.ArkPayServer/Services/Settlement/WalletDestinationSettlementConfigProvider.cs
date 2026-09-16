using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

/// <summary>
/// Turns each wallet's configured sweep destination into a settlement rule, replacing the
/// plugin-owned <c>DestinationSweepPolicy</c>.
/// </summary>
/// <remarks>
/// A SingleKey wallet without a destination settles to itself rather than nowhere: every receive
/// address it hands out is a hash-locked contract with its own preimage, so without that the
/// balance stays scattered across one address per invoice. A destination flagged
/// <see cref="DestinationSafety.PendingConfirmationMetadataKey"/> is treated as absent — the
/// operator rotated its signer and the merchant has not re-confirmed the address, so paying it
/// could pay somewhere the store no longer controls.
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
    {
        if (!string.IsNullOrEmpty(wallet.Destination) && !IsPendingConfirmation(wallet))
            return SettlementDestination.Ark(wallet.Destination);

        return wallet.WalletType == WalletType.SingleKey
            ? SettlementDestination.ArkSelf()
            : null;
    }

    private static bool IsPendingConfirmation(ArkWalletInfo wallet)
        => wallet.Metadata?.ContainsKey(DestinationSafety.PendingConfirmationMetadataKey) == true;
}
