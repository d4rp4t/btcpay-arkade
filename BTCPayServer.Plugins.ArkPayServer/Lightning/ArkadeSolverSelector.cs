using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Where to meet a solver, and the terms it published for meeting there.</summary>
/// <param name="Pubkey">The solver's x-only discovery key — who to address.</param>
/// <param name="Relay">The relay to meet on — where to dial.</param>
/// <param name="Market">The market that was chosen, for its limits and its price.</param>
public sealed record SolverRendezvous(string Pubkey, Uri Relay, IndexedMarket? Market);

/// <summary>
/// Chooses which solver this store's Lightning corridor trades with.
/// </summary>
/// <remarks>
/// <para>
/// Three questions, in order: does a solver serve the Lightning corridor at all, does it serve a
/// trade this size, and of the ones that do, which is cheapest. The registry answers all three
/// without a negotiation, because a card states its corridor, its bounds and its fee up front.
/// </para>
/// <para>
/// Configuration still wins where it is set. A named solver is how a development stack works — its
/// solver mints a fresh identity per run, so no registry can list it — and it is also the escape
/// hatch for an operator who wants one particular counterparty rather than the cheapest.
/// </para>
/// </remarks>
public sealed class ArkadeSolverSelector(
    ArkadeSolverOptions options,
    string? networkName,
    SolverDiscoveryService? discovery = null)
{
    /// <summary>The rail a Lightning corridor settles its quote side on.</summary>
    public const string LightningCorridor = "lightning";

    /// <summary>The rail an onchain corridor settles its quote side on.</summary>
    /// <remarks>
    /// Bitcoin L1. Both legs are still bitcoin — what differs is the rail the quote side settles on,
    /// which is exactly what the corridor names.
    /// </remarks>
    public const string OnchainCorridor = "onchain";

    /// <summary>Both legs of the corridor are bitcoin; only the rails differ.</summary>
    private const string BitcoinAssetId = "btc";

    /// <summary>Whether a solver was named outright, making discovery unnecessary.</summary>
    public bool HasExplicitSolver =>
        !string.IsNullOrWhiteSpace(options.RelayUri) && !string.IsNullOrWhiteSpace(options.SolverPubkey);

    /// <summary>Whether this deployment could reach a solver at all, by either route.</summary>
    /// <remarks>
    /// Cheap and synchronous on purpose: it answers "is this deployment wired for Lightning", which
    /// several callers ask while rendering a page. Whether a solver is actually listed, and serves a
    /// given size, is <see cref="SelectAsync"/>'s question and needs the network.
    /// </remarks>
    public bool CanReachASolver => HasExplicitSolver || (discovery is not null && networkName is not null);

    /// <summary>Pick the cheapest listed solver on the Lightning corridor.</summary>
    /// <param name="amountSats">The trade size, used to rank on total fee rather than on spread.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>Where to meet the chosen solver, or <c>null</c> when none is listed.</returns>
    /// <remarks>
    /// The amount ranks but does not exclude. A card states its bounds per side, and which side
    /// applies depends on the direction of the trade — the leg the solver pays out — while this
    /// method is called from both. Comparing a Lightning-side amount against the Arkade-side bounds
    /// would admit sizes a solver refuses and refuse sizes it would take, both silently. The quote
    /// settles the question exactly, and refuses with the solver's own reason.
    /// </remarks>
    public Task<SolverRendezvous?> SelectAsync(
        long amountSats, CancellationToken cancellationToken = default) =>
        SelectAsync(amountSats, LightningCorridor, cancellationToken);

    /// <summary>Pick the cheapest listed solver on a named corridor.</summary>
    /// <param name="amountSats">The trade size, used to rank on total fee rather than on spread.</param>
    /// <param name="quoteCorridor">The rail the quote side settles on.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>Where to meet the chosen solver, or <c>null</c> when none is listed.</returns>
    public async Task<SolverRendezvous?> SelectAsync(
        long amountSats, string quoteCorridor, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver && Uri.TryCreate(options.RelayUri, UriKind.Absolute, out var configured))
        {
            return new SolverRendezvous(options.SolverPubkey!, configured, null);
        }

        var market = (await MarketsAsync(quoteCorridor, cancellationToken))
            .OrderBy(m => m.TotalFeeOn(amountSats))
            .ThenBy(m => m.FeeBps)
            .FirstOrDefault();

        if (market?.DiscoveryPubkey is not { Length: > 0 } pubkey)
        {
            return null;
        }

        // A market that got this far is reachable by construction, but the relay list is a
        // stranger's data and an unparseable entry should drop the candidate rather than throw.
        return market.Transports?.Nostr?.Relays
            .Select(r => Uri.TryCreate(r, UriKind.Absolute, out var parsed) ? parsed : null)
            .FirstOrDefault(r => r is not null) is { } relay
            ? new SolverRendezvous(pubkey, relay, market)
            : null;
    }

    /// <summary>Whether anyone will trade this corridor at all.</summary>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <remarks>
    /// The question worth asking before offering Lightning at a checkout: not whether a particular
    /// amount clears, which only a quote can answer, but whether there is a counterparty. A named
    /// solver is taken at its word.
    /// </remarks>
    public Task<bool> HasLightningSolverAsync(CancellationToken cancellationToken = default) =>
        HasSolverForAsync(LightningCorridor, cancellationToken);

    /// <summary>Whether anyone will trade a named corridor at all.</summary>
    /// <param name="quoteCorridor">The rail the quote side settles on.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <remarks>
    /// A named solver is taken at its word for every corridor: it publishes no card, so there is
    /// nothing to check it against, and refusing it here would make a development stack untestable.
    /// </remarks>
    public async Task<bool> HasSolverForAsync(
        string quoteCorridor, CancellationToken cancellationToken = default) =>
        HasExplicitSolver || (await MarketsAsync(quoteCorridor, cancellationToken)).Count > 0;

    /// <summary>The widest amount range a payer can be asked for on this corridor.</summary>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>The range in sats, or <c>null</c> when nothing can be said about it.</returns>
    /// <remarks>
    /// <para>
    /// Read off the quote side, because that is the Lightning leg — what the payer actually sends.
    /// The base side bounds the Arkade leg, which is a different number once the solver's fee is in
    /// it, and advertising one in place of the other is how a payer is invited to send an amount
    /// that cannot settle.
    /// </para>
    /// <para>
    /// The union across solvers rather than one solver's range, because the choice is made per
    /// payment: an amount only one of them serves is still an amount this store can be paid. A named
    /// solver publishes nothing, so it constrains nothing here.
    /// </para>
    /// </remarks>
    public Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default) =>
        ServedRangeAsync(LightningCorridor, cancellationToken);

    /// <summary>The widest amount range a payer can be asked for on a named corridor.</summary>
    /// <param name="quoteCorridor">The rail the quote side settles on.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>The range in sats, or <c>null</c> when nothing can be said about it.</returns>
    public async Task<(long Min, long Max)?> ServedRangeAsync(
        string quoteCorridor, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver)
        {
            return null;
        }

        var bounded = (await MarketsAsync(quoteCorridor, cancellationToken))
            .Where(m => m.MaxQuoteAmount > 0)
            .ToList();

        return bounded.Count == 0
            ? null
            : (bounded.Min(m => m.MinQuoteAmount), bounded.Max(m => m.MaxQuoteAmount));
    }

    /// <summary>A corridor's canonical identity: arkade bitcoin against bitcoin on another rail.</summary>
    /// <param name="quoteCorridor">The rail the quote side settles on.</param>
    private static string PairFor(string quoteCorridor) =>
        $"{SolverMarket.ArkadeCorridor}:{BitcoinAssetId}/{quoteCorridor}:{BitcoinAssetId}";

    /// <summary>Every listed market on a corridor that can actually be dialled.</summary>
    /// <param name="quoteCorridor">The rail the quote side settles on.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <remarks>
    /// The corridor is a parameter rather than a field because nothing else about this selector
    /// depends on it: the same registry, the same network and the same named-solver escape hatch
    /// answer for every rail. A second instance per corridor would duplicate the ranking and the
    /// relay parsing, which are the parts worth having in one place.
    /// </remarks>
    private async Task<IReadOnlyList<IndexedMarket>> MarketsAsync(
        string quoteCorridor, CancellationToken cancellationToken)
    {
        if (discovery is null || networkName is null)
        {
            return [];
        }

        var wanted = PairFor(quoteCorridor);
        return (await DiscoverAsync(cancellationToken))
            .Where(m => m.PairKey() == wanted)
            .Where(m => m.DiscoveryPubkey is { Length: > 0 } && m.Transports?.Nostr?.Relays.Count > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<IndexedMarket>> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await discovery!.DiscoverMarketsAsync(networkName!, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registry client already falls back to its last good copy, so reaching here means
            // there has never been one. No solver was found, which is not a failed payment: the
            // caller withdraws the offer, the same thing it does for an empty registry.
            return [];
        }
    }

    /// <summary>The registry's name for a chain, or <c>null</c> when it publishes none.</summary>
    /// <param name="network">The chain this deployment runs on.</param>
    /// <remarks>
    /// Kept here rather than taken from <c>ChainName.ToString()</c> because the two vocabularies only
    /// coincide by accident: the registry says <c>bitcoin</c> where NBitcoin says <c>Main</c>.
    /// </remarks>
    public static string? RegistryNetworkName(ChainName network) =>
        network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName ? "bitcoin"
        : network == NBitcoin.Bitcoin.Instance.Signet.ChainName ? "signet"
        : network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName ? "mutinynet"
        : network == ChainName.Regtest ? "regtest"
        : null;
}
