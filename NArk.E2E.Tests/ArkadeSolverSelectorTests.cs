using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.ArkadeIntents.Services;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// The plugin's solver selection, run against the registry that is actually published.
/// </summary>
/// <remarks>
/// Carries the <c>SolverRegistry</c> category so CI skips it: it reaches the public internet, and a
/// slow GitHub Pages or a delisted solver is not a reason to fail a build. Run it by category when
/// changing selection, or to answer "can this plugin find and dial a Lightning solver right now".
/// </remarks>
[Trait("Category", "SolverRegistry")]
public class ArkadeSolverSelectorTests
{
    private static ArkadeSolverSelector Selector(ChainName network) => new(
        new ArkadeSolverOptions(),
        ArkadeSolverSelector.RegistryNetworkName(network),
        new SolverDiscoveryService(new HttpClient()));

    [Theory]
    [InlineData("mainnet")]
    [InlineData("mutinynet")]
    [Trait("Category", "SolverPresets")]
    public void A_hosted_network_ships_its_emulator_endpoint(string network)
    {
        // HasEmulator gates the whole corridor registration, discovery included, so an empty preset
        // does not degrade anything gracefully — it turns Lightning off and says nothing.
        var chain = network == "mainnet"
            ? Bitcoin.Instance.Mainnet.ChainName
            : Bitcoin.Instance.Mutinynet.ChainName;

        Assert.True(ArkadeSolverOptions.ForNetwork(chain).HasEmulator);
    }

    [Fact]
    [Trait("Category", "SolverPresets")]
    public void Every_network_with_a_registry_can_reach_a_solver_unconfigured()
    {
        // The end of the chain the preset feeds: a stock install on a hosted network should find a
        // solver without anyone editing ark.json.
        foreach (var chain in new[] { Bitcoin.Instance.Mainnet.ChainName, Bitcoin.Instance.Mutinynet.ChainName })
        {
            var options = ArkadeSolverOptions.ForNetwork(chain);
            var selector = new ArkadeSolverSelector(
                options,
                ArkadeSolverSelector.RegistryNetworkName(chain),
                new SolverDiscoveryService(new HttpClient()));

            Assert.False(selector.HasExplicitSolver);
            Assert.True(selector.CanReachASolver, $"{chain} cannot reach a solver out of the box");
        }
    }

    [Fact]
    public async Task Selects_a_diallable_lightning_solver_on_mutinynet()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.NotNull(rendezvous);
        Assert.NotEmpty(rendezvous!.Pubkey);
        Assert.Contains(rendezvous.Relay.Scheme, new[] { "ws", "wss" });
        Assert.Equal("lightning", rendezvous.Market?.QuoteCorridor);
    }

    [Fact]
    public async Task Selects_regardless_of_size_and_leaves_the_bounds_to_the_quote()
    {
        // A card states bounds per side and which side applies depends on the direction of the
        // trade, so selection ranks on the amount but does not exclude on it. A whole bitcoin is far
        // outside every published Lightning corridor and still picks a counterparty; the quote is
        // what refuses it, with the solver's own reason.
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        Assert.NotNull(await selector.SelectAsync(100_000_000));
        Assert.True(await selector.HasLightningSolverAsync());
    }

    [Fact]
    public async Task Reports_the_range_it_can_be_paid_in()
    {
        var selector = Selector(Bitcoin.Instance.Mutinynet.ChainName);

        var range = await selector.ServedRangeAsync();

        Assert.NotNull(range);
        Assert.True(range!.Value.Min > 0);
        Assert.True(range.Value.Max > range.Value.Min);

        // The quote side, which is the Lightning leg — what a payer is actually asked for. The base
        // side bounds the Arkade leg, a different number once the solver's fee is in it.
        var market = (await selector.SelectAsync(10_000))!.Market!;
        Assert.Equal(market.MinQuoteAmount, range.Value.Min);
        Assert.Equal(market.MaxQuoteAmount, range.Value.Max);
    }

    // ─── Against a registry written here, not a published one ─────────
    //
    // The tests above answer "can this plugin find a solver right now", which is worth asking and
    // is why they reach the internet. They cannot pin selection itself: what they assert holds
    // whichever market is picked, and the published cards are somebody else's data — if two solvers
    // ever publish the same bounds on both sides, an assertion here stops distinguishing anything
    // without failing. These two supply their own registry so the behaviour is pinned by
    // construction.

    /// <summary>A registry answering every request with <paramref name="marketsJson"/>.</summary>
    private static ArkadeSolverSelector SelectorOver(string marketsJson) => new(
        new ArkadeSolverOptions(),
        "mutinynet",
        new SolverDiscoveryService(new HttpClient(new CannedRegistry($$"""
        {
          "version": 0,
          "network": "mutinynet",
          "generated_at": 1783958400,
          "commit": "deadbeef",
          "markets": [ {{marketsJson}} ]
        }
        """))));

    /// <summary>One dialable arkade:btc/lightning:btc market.</summary>
    private static string LightningMarket(
        string solver, int feeBps, string feeFlat = "0",
        string minQuote = "1000", string maxQuote = "5000000",
        string minBase = "1000", string maxBase = "5000000",
        string corridor = ArkadeSolverSelector.LightningCorridor) => $$"""
        {
          "pair": "BTC/BTC",
          "solver": "{{solver}}",
          "discovery_pubkey": "{{solver}}-pubkey",
          "base_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_asset": { "id": "btc", "name": "Bitcoin", "ticker": "BTC", "decimals": 8 },
          "quote_corridor": "{{corridor}}",
          "fee_bps": {{feeBps}},
          "fee_flat": "{{feeFlat}}",
          "min_base_amount": "{{minBase}}",
          "max_base_amount": "{{maxBase}}",
          "min_quote_amount": "{{minQuote}}",
          "max_quote_amount": "{{maxQuote}}",
          "transports": { "nostr": { "relays": ["wss://relay.example"] } }
        }
        """;

    [Fact]
    public async Task Picks_the_market_that_costs_the_payer_least_at_this_size()
    {
        // The flat fee is why the spread alone cannot rank: at 10k sats the 10bps card charges 10
        // plus its flat 500, and the 60bps card charges 60 — so the wider spread is the cheaper
        // trade here, and ordering on `fee_bps` would pick the dearer one while looking correct.
        var selector = SelectorOver(string.Join(",",
            LightningMarket("flat-heavy", feeBps: 10, feeFlat: "500"),
            LightningMarket("spread-only", feeBps: 60)));

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.Equal("spread-only-pubkey", rendezvous!.Pubkey);
    }

    [Fact]
    public async Task The_cheapest_market_changes_with_the_size()
    {
        // The other side of the same arithmetic: at 1,000,000 sats the flat 500 is noise against a
        // 60bps spread, so the ranking has to invert. A test at one size only would pass against an
        // implementation that ignored the amount entirely.
        var selector = SelectorOver(string.Join(",",
            LightningMarket("flat-heavy", feeBps: 10, feeFlat: "500"),
            LightningMarket("spread-only", feeBps: 60)));

        var rendezvous = await selector.SelectAsync(1_000_000);

        Assert.Equal("flat-heavy-pubkey", rendezvous!.Pubkey);
    }

    [Fact]
    public async Task The_advertised_range_is_the_leg_the_payer_sends_on()
    {
        // Quote is the Lightning side — what a payer is asked for. Base bounds the Arkade side, a
        // different number once the solver's fee is in it. The two are given deliberately
        // incompatible values so reading the wrong one cannot coincide with reading the right one.
        var selector = SelectorOver(LightningMarket(
            "solo", feeBps: 10,
            minQuote: "2000", maxQuote: "300000",
            minBase: "7", maxBase: "9"));

        var range = await selector.ServedRangeAsync();

        Assert.Equal((2000L, 300000L), range);
    }

    [Fact]
    public async Task A_corridor_is_never_answered_with_another_ones_solver()
    {
        // Both legs are bitcoin on every one of these corridors, so the only thing separating an
        // onchain market from a Lightning one is the rail its quote side settles on. Reading that
        // wrong offers a payer an onchain address for a corridor the solver serves over Lightning.
        var selector = SelectorOver(string.Join(",",
            LightningMarket("ln-solver", feeBps: 10),
            LightningMarket("onchain-solver", feeBps: 90,
                corridor: ArkadeSolverSelector.OnchainCorridor)));

        var lightning = await selector.SelectAsync(10_000, ArkadeSolverSelector.LightningCorridor);
        var onchain = await selector.SelectAsync(10_000, ArkadeSolverSelector.OnchainCorridor);

        Assert.Equal("ln-solver-pubkey", lightning!.Pubkey);

        // The dearer card wins here, which is the point: it is the only one on this rail, so
        // ranking cannot be what picked it.
        Assert.Equal("onchain-solver-pubkey", onchain!.Pubkey);
    }

    [Fact]
    public async Task A_corridor_nobody_serves_has_no_solver()
    {
        var selector = SelectorOver(LightningMarket("ln-solver", feeBps: 10));

        Assert.True(await selector.HasSolverForAsync(ArkadeSolverSelector.LightningCorridor));
        Assert.False(await selector.HasSolverForAsync(ArkadeSolverSelector.OnchainCorridor));
        Assert.Null(await selector.SelectAsync(10_000, ArkadeSolverSelector.OnchainCorridor));
    }

    /// <summary>Answers every request with one canned body, so no test here touches the network.</summary>
    private sealed class CannedRegistry(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task A_named_solver_skips_discovery_entirely()
    {
        // The development case: a stack's solver mints a fresh identity per run, so no registry can
        // list it. Naming one has to work with no registry reachable at all.
        var selector = new ArkadeSolverSelector(
            new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                SolverPubkey = "3f831510a6d7678d0c90d7d6fbc4057720517e2e30681ef4c87cc57aaf57e8d5",
            },
            networkName: null);

        var rendezvous = await selector.SelectAsync(10_000);

        Assert.NotNull(rendezvous);
        Assert.Equal("ws://localhost:7777/", rendezvous!.Relay.ToString());
        Assert.Null(rendezvous.Market);
        Assert.True(await selector.HasLightningSolverAsync());
        Assert.Null(await selector.ServedRangeAsync());
    }
}
