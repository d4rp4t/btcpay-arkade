using Xunit;
using BTCPayServer.Plugins.ArkPayServer.Services.Settlement;
using NArk.Abstractions.Settlement;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;

namespace NArk.Tests;

/// <summary>
/// Which wallets get a settlement rule. The cases that matter are the ones where an extra rule
/// spins (a wallet settling to itself re-fires on its own output) or a missing one strands funds.
/// </summary>
public class WalletDestinationSettlementConfigProviderTests
{
    private const string ArkDestination =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nkgrmhu6tj2vuh3q6nzhlg8rlnf9gcqvqkn3wdxyxkpn8hcnszeaeq";

    // A destination is honoured whatever the wallet type: only the no-destination case differs,
    // where SingleKey consolidates locally and HD has no single address to consolidate onto.
    [Theory]
    [InlineData(WalletType.SingleKey)]
    [InlineData(WalletType.HD)]
    public async Task Wallet_with_a_destination_settles_there(WalletType walletType)
    {
        var provider = Provider(Wallet("w1", ArkDestination, walletType));

        var config = Assert.Single(await provider.GetConfigs());

        Assert.Equal("w1", config.WalletId);
        Assert.Equal(SettlementNetworks.Ark, config.Destination.Network);
        Assert.Equal(ArkDestination, config.Destination.Address);
        Assert.Equal(0, config.Threshold);
        Assert.True(config.Enabled);
    }

    [Fact]
    public async Task SingleKey_wallet_without_a_destination_gets_no_rule()
    {
        // Settling a wallet to itself satisfies its own threshold with its own output, so the rule
        // would re-fire forever. SingleKeyConsolidationSweepPolicy does this job coin-by-coin.
        var provider = Provider(Wallet("w1", destination: null, WalletType.SingleKey));

        Assert.Empty(await provider.GetConfigs());
    }

    [Fact]
    public async Task Hd_wallet_without_a_destination_settles_nowhere()
    {
        var provider = Provider(Wallet("w1", destination: null, WalletType.HD));

        Assert.Empty(await provider.GetConfigs());
    }

    [Fact]
    public async Task Destination_pending_reconfirmation_gets_no_rule()
    {
        // The operator rotated its signer and the merchant has not re-confirmed the address, so
        // paying it could pay somewhere the store no longer controls.
        var provider = Provider(Wallet("w1", ArkDestination, WalletType.SingleKey, pendingConfirmation: true));

        Assert.Empty(await provider.GetConfigs());
    }

    [Fact]
    public async Task Hd_wallet_with_a_destination_pending_reconfirmation_gets_no_rule()
    {
        var provider = Provider(Wallet("w1", ArkDestination, WalletType.HD, pendingConfirmation: true));

        Assert.Empty(await provider.GetConfigs());
    }

    [Fact]
    public async Task An_unrelated_metadata_key_does_not_suppress_the_destination()
    {
        var wallet = Wallet("w1", ArkDestination) with
        {
            Metadata = new Dictionary<string, string> { ["sync:cursor"] = "1" }
        };

        var config = Assert.Single(await Provider(wallet).GetConfigs());

        Assert.Equal(ArkDestination, config.Destination.Address);
    }

    [Fact]
    public async Task A_wallet_id_narrows_the_result_to_that_wallet()
    {
        var provider = Provider(
            Wallet("w1", ArkDestination),
            Wallet("w2", ArkDestination));

        var config = Assert.Single(await provider.GetConfigs("w2"));

        Assert.Equal("w2", config.WalletId);
    }

    private static WalletDestinationSettlementConfigProvider Provider(params ArkWalletInfo[] wallets)
        => new(new FakeWalletStorage(wallets));

    private static ArkWalletInfo Wallet(
        string id,
        string? destination,
        WalletType type = WalletType.SingleKey,
        bool pendingConfirmation = false)
        => new(
            Id: id,
            Secret: null,
            Destination: destination,
            WalletType: type,
            AccountDescriptor: null,
            LastUsedIndex: 0,
            Metadata: pendingConfirmation
                ? new Dictionary<string, string> { [DestinationSafety.PendingConfirmationMetadataKey] = "deadbeef" }
                : null);

    /// <summary>Only the read paths the provider uses are implemented.</summary>
    private sealed class FakeWalletStorage(IReadOnlyList<ArkWalletInfo> wallets) : IWalletStorage
    {
        public event EventHandler<ArkWalletInfo>? WalletSaved { add { } remove { } }
        public event EventHandler<string>? WalletDeleted { add { } remove { } }

        public Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<ArkWalletInfo>>(wallets.ToHashSet());

        public Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(
            IEnumerable<string> walletIds, CancellationToken ct = default)
        {
            var ids = walletIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<ArkWalletInfo>>(
                wallets.Where(w => ids.Contains(w.Id)).ToList());
        }

        public Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteWallet(string walletId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetMetadataValue(string walletId, string key, string? value, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
