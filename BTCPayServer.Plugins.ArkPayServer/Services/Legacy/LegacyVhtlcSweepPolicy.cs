using System.Runtime.CompilerServices;
using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Core.Sweeper;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Legacy;

/// <summary>
/// Selects pre-migration VHTLC coins for the sweeper.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the SDK's <c>NArk.Swaps</c> package when that package was dropped. Nothing here was
/// ever Boltz-specific — it selects on the contract type — and dropping it with the rest of the
/// swaps code would have quietly stopped every VHTLC still holding sats from being swept. That is
/// the failure mode this file exists to prevent: no error, no log, just funds that stop moving.
/// </para>
/// <para>
/// Legacy on purpose, and expected to select nothing on a store that never ran a Boltz swap. It can
/// be deleted once no wallet has a VHTLC left, which is a question about the chain rather than about
/// the code.
/// </para>
/// </remarks>
public class LegacyVhtlcSweepPolicy : ISweepPolicy
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ArkCoin> SweepAsync(
        IEnumerable<ArkCoin> coins,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var coin in coins.Where(c => c.Contract is VHTLCContract))
        {
            yield return coin;
        }
    }
}
