using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transformers;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Legacy;

/// <summary>
/// Makes a pre-migration VHTLC spendable: the claim leaf while we hold the preimage, the refund leaf
/// once the chain's clock passes the locktime.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the SDK's <c>NArk.Swaps</c> package when that package was dropped. It touches nothing
/// Boltz-specific — <see cref="VHTLCContract"/> lives in <c>NArk.Core</c> and stays — but it is the
/// only thing that turns one of those contracts into a spendable coin. Losing it with the rest of
/// the swaps code would have stranded every in-flight refund without an error to show for it.
/// </para>
/// <para>
/// Nothing creates a VHTLC any more; the Lightning corridor negotiates a covenant with a solver
/// instead. This is drain-only, and can go once no wallet holds one.
/// </para>
/// </remarks>
public class LegacyVhtlcContractTransformer(
    IWalletProvider walletProvider,
    IBitcoinBlockchain chainTimeProvider,
    ILogger<LegacyVhtlcContractTransformer>? logger = null) : IContractTransformer
{
    /// <inheritdoc />
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not VHTLCContract htlc) return false;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);
        if (addressProvider is null) return false;

        // Claim: we hold the secret and the covenant pays us.
        if (htlc.Preimage is not null && await addressProvider.IsOurs(htlc.Receiver))
        {
            return await walletProvider.GetSignerAsync(walletIdentifier) is not null;
        }

        // Refund: we funded it and the deadline has passed. Measured against the CHAIN's clock —
        // an absolute locktime matures on median time past, which trails real time, so our own
        // clock would offer a spend the chain still refuses.
        if (await RefundIsOpenAsync(htlc) && await addressProvider.IsOurs(htlc.Sender))
        {
            return await walletProvider.GetSignerAsync(walletIdentifier) is not null;
        }

        // Neither path is open: this contract is ours to watch, not to spend.
        return false;
    }

    /// <inheritdoc />
    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var htlc = (VHTLCContract)contract;
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier)
            ?? throw new InvalidOperationException(
                $"Wallet '{walletIdentifier}' has no address provider, so a VHTLC cannot be spent.");

        if (htlc.Preimage is not null && await addressProvider.IsOurs(htlc.Receiver))
        {
            logger?.LogInformation(
                "VHTLC claim: wallet={WalletId}, receiver={Receiver}, sender={Sender}, outpoint={Outpoint}",
                walletIdentifier, htlc.Receiver, htlc.Sender, vtxo.OutPoint);
            return new ArkCoin(
                walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
                vtxo.OutPoint, vtxo.TxOut, htlc.Receiver, htlc.CreateClaimScript(),
                new WitScript(Op.GetPushOp(htlc.Preimage)), null, null, vtxo.Swept, vtxo.Unrolled);
        }

        if (await RefundIsOpenAsync(htlc) && await addressProvider.IsOurs(htlc.Sender))
        {
            logger?.LogInformation(
                "VHTLC refund: wallet={WalletId}, sender={Sender}, receiver={Receiver}, outpoint={Outpoint}, "
                + "refundLocktime={RefundLocktime}",
                walletIdentifier, htlc.Sender, htlc.Receiver, vtxo.OutPoint, htlc.RefundLocktime);
            return new ArkCoin(
                walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
                vtxo.OutPoint, vtxo.TxOut, htlc.Sender, htlc.CreateRefundWithoutReceiverScript(),
                null, htlc.RefundLocktime, null, vtxo.Swept, vtxo.Unrolled);
        }

        throw new InvalidOperationException("CanTransform should have returned false for this coin");
    }

    /// <summary>Whether the refund leaf has matured, judged on the chain's clock rather than ours.</summary>
    private async Task<bool> RefundIsOpenAsync(VHTLCContract htlc)
    {
        var chainTime = await chainTimeProvider.GetChainTime();
        return htlc.RefundLocktime.IsTimeLock
            ? htlc.RefundLocktime.Date < chainTime.Timestamp
            : chainTime.Height >= htlc.RefundLocktime.Value;
    }
}
