using System.Text.Json.Serialization;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Solver and endpoint settings for the Arkade Lightning corridors, read from <c>ark.json</c>.</summary>
public class ArkadeSolverOptions
{
    // Only consulted alongside SolverPubkey; a relay without a key falls through to discovery.
    [JsonPropertyName("solver-relay")]
    public string? RelayUri { get; set; }

    // Set with RelayUri to pin one solver and skip the registry (a dev solver mints a fresh identity per run).
    [JsonPropertyName("solver-pubkey")]
    public string? SolverPubkey { get; set; }

    // Optional: with a daemon a second party can finish a claim while this server is down.
    [JsonPropertyName("covclaimd")]
    public string? CovclaimdUri { get; set; }

    [JsonPropertyName("emulator")]
    public string? EmulatorUri { get; set; }

    // Applies to every quote, carded or not: a named solver publishes no card, and the SDK's card check
    // is skipped when the quote's own pair field doesn't resolve.
    [JsonPropertyName("max-fee-bps")]
    public int? MaxFeeBps { get; set; }

    [JsonPropertyName("max-fee-flat-sats")]
    public long? MaxFeeFlatSats { get; set; }

    public const int DefaultMaxFeeBps = 100;
    public const long DefaultMaxFeeFlatSats = 500;

    public bool HasEmulator => !string.IsNullOrWhiteSpace(EmulatorUri);

    public long MaxFeeOn(long depositedSats) =>
        depositedSats * (MaxFeeBps ?? DefaultMaxFeeBps) / 10_000 + (MaxFeeFlatSats ?? DefaultMaxFeeFlatSats);

    // Emulator URIs only satisfy the HasEmulator gate; nothing dials them, since the corridor needs just the
    // co-signer key the SDK pins per network. The gate should check for a pinned key, and these can then go.
    // Signet has no emulator deployment or pinned key, so it is left out on purpose.
    public static ArkadeSolverOptions ForNetwork(ChainName network)
    {
        // todo(6.09.2026): this one should be in the network config
        if (network == ChainName.Regtest)
        {
            return new ArkadeSolverOptions
            {
                RelayUri = "ws://localhost:7777",
                CovclaimdUri = "http://localhost:7271",
                EmulatorUri = "http://localhost:7073",
            };
        }

        if (network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
        {
            return new ArkadeSolverOptions { EmulatorUri = "https://emulator.arkade.sh" };
        }

        if (network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
        {
            return new ArkadeSolverOptions { EmulatorUri = "https://emulator.mutinynet.arkade.sh" };
        }

        return new ArkadeSolverOptions();
    }

    // Per field, so setting only solver-pubkey on regtest keeps the preset relay and emulator.
    public static ArkadeSolverOptions Merge(ArkadeSolverOptions preset, ArkadeSolverOptions? file) =>
        new()
        {
            RelayUri = Pick(file?.RelayUri, preset.RelayUri),
            SolverPubkey = Pick(file?.SolverPubkey, preset.SolverPubkey),
            CovclaimdUri = Pick(file?.CovclaimdUri, preset.CovclaimdUri),
            EmulatorUri = Pick(file?.EmulatorUri, preset.EmulatorUri),
            MaxFeeBps = file?.MaxFeeBps ?? preset.MaxFeeBps,
            MaxFeeFlatSats = file?.MaxFeeFlatSats ?? preset.MaxFeeFlatSats,
        };

    private static string? Pick(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
