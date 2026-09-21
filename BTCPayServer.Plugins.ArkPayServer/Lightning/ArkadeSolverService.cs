using System.Net.Http.Json;
using System.Text.Json;
using NArk.ArkadeIntents.Rfq;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Opens and closes the transport to the solver <see cref="ArkadeSolverSelector"/> picks per trade.</summary>
public class ArkadeSolverService(
    ArkadeSolverOptions options,
    ArkadeSolverSelector selector,
    IHttpClientFactory httpClientFactory)
{
    private string? _covclaimdKey;

    public bool IsConfigured => selector.CanReachASolver;

    // No size check here: which card bound applies depends on trade direction; the quote answers it exactly.
    public Task<bool> HasSolverAsync(CancellationToken cancellationToken = default) =>
        selector.HasLightningSolverAsync(cancellationToken);

    public Task<bool> HasSolverForAsync(
        string quoteCorridor, CancellationToken cancellationToken = default) =>
        selector.HasSolverForAsync(quoteCorridor, cancellationToken);

    public Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default) =>
        selector.ServedRangeAsync(cancellationToken);

    public string? RelayUri => selector.HasExplicitSolver ? options.RelayUri : null;

    public string? SolverPubkey => selector.HasExplicitSolver ? options.SolverPubkey : null;

    // IRfqTransport is not IDisposable but the Nostr one is; disposing here avoids leaking a relay socket per invoice.
    public Task<T> WithTransportAsync<T>(
        long amountSats,
        Func<IRfqTransport, Task<T>> negotiate,
        CancellationToken cancellationToken = default) =>
        WithTransportAsync(amountSats, ArkadeSolverSelector.LightningCorridor, negotiate, cancellationToken);

    // Asked for by name: a solver listed for Lightning never agreed to watch an onchain address.
    public async Task<T> WithTransportAsync<T>(
        long amountSats,
        string quoteCorridor,
        Func<IRfqTransport, Task<T>> negotiate,
        CancellationToken cancellationToken = default)
    {
        var rendezvous = await selector.SelectAsync(amountSats, quoteCorridor, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No Arkade swap solver is listed for the {quoteCorridor} corridor on this network, " +
                "and none is named in the Arkade network configuration. Set solver-relay and " +
                "solver-pubkey to name one directly.");

        var transport = Open(rendezvous);
        try
        {
            return await negotiate(transport);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }

    // The scheme picks the transport; a discovered solver is always reached over a relay.
    private IRfqTransport Open(SolverRendezvous rendezvous) =>
        rendezvous.Relay.Scheme is var scheme
        && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps)
            ? new HttpRfqTransport(httpClientFactory.CreateClient(), rendezvous.Relay)
            : new NostrRfqTransport(rendezvous.Relay, rendezvous.Pubkey);

    // Without a daemon the packet is sealed to a throwaway key: the wire field is satisfied and only we can
    // claim. Being down for the whole window fails the payment but loses no funds.
    // The daemon generates its key at startup, so it is fetched (and cached per lifetime) rather than configured.
    // The throwaway key is not cached: reuse would link swaps on the wire.
    public async Task<string> ResolveClaimRecipientAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.CovclaimdUri))
        {
            return new Key().PubKey.Compress().ToHex();
        }

        if (_covclaimdKey is { } cached)
        {
            return cached;
        }

        using var http = httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(options.CovclaimdUri.TrimEnd('/') + "/");

        var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey", cancellationToken);
        if (!doc.TryGetProperty("covclaimd_pub_key", out var key) || key.GetString() is not { Length: > 0 } hex)
        {
            throw new InvalidOperationException(
                $"The claim daemon at {options.CovclaimdUri} answered without a public key.");
        }

        return _covclaimdKey = hex;
    }
}
