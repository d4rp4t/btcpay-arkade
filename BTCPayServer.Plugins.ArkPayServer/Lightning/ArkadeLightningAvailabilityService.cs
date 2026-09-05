using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Decides whether a store should be offered Lightning at checkout.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and only the second is interesting. Whether a store is on Arkade Lightning is
/// read from its connection string; whether Arkade Lightning can currently settle anything is
/// whether a solver is configured to settle it with.
/// </para>
/// <para>
/// It deliberately does not pre-check the amount, which is why it is no longer named for limits.
/// The Boltz integration this replaced could, because Boltz published its limits as an endpoint;
/// an Arkade solver's terms are per-quote, and the only way to learn them is to open a negotiation
/// — far too expensive to do while rendering a checkout page, and stale by the time the customer
/// pays. An amount a solver will not take is refused at the point of quoting, with the solver's own
/// reason attached, which is a better error than a guess made minutes earlier.
/// </para>
/// </remarks>
public class ArkadeLightningAvailabilityService : IDisposable
{
    private readonly ArkadeSolverService _solver;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly IMemoryCache _memoryCache;
    private readonly StoreRepository _storeRepository;
    private readonly CompositeDisposable _leases = new();

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

    public ArkadeLightningAvailabilityService(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        EventAggregator eventAggregator,
        IMemoryCache memoryCache,
        StoreRepository storeRepository,
        ArkadeSolverService solver)
    {
        _solver = solver;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _memoryCache = memoryCache;
        _storeRepository = storeRepository;

        // A store's connection string is exactly what this caches, so an edit to it has to evict.
        _leases.Add(eventAggregator.Subscribe<StoreEvent.Updated>(ev => ClearStoreCache(ev.StoreId)));
    }

    private static string GetStoreCacheKey(string storeId) => $"arkade-lightning-{storeId}";

    /// <summary>Whether this store's Lightning method is served by this plugin.</summary>
    /// <param name="store">The store to inspect. A null store is nobody's Arkade store.</param>
    public bool IsStoreUsingArkadeLightning(StoreData? store)
    {
        if (store?.Id is not { } storeId)
        {
            return false;
        }

        if (_memoryCache.TryGetValue<bool>(GetStoreCacheKey(storeId), out var cached))
        {
            return cached;
        }

        var usesArkade = UsesArkadeLightning(store);
        _memoryCache.Set(GetStoreCacheKey(storeId), usesArkade, CacheExpiry);
        return usesArkade;
    }

    /// <summary>Whether Lightning should be offered on this store.</summary>
    /// <param name="storeId">The store the invoice belongs to.</param>
    /// <param name="cancellationToken">Observed before the store lookup.</param>
    /// <remarks>
    /// Takes no amount, because none would change the answer — see the note on the class. The
    /// top-up case needs no special handling either: an invoice with no amount and an invoice with
    /// one are offered the method on the same grounds, which is whether anything can settle it.
    /// </remarks>
    public async Task<bool> ShouldOfferLightningAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        // Somebody else's Lightning, so somebody else's decision.
        if (!await IsStoreUsingArkadeLightningAsync(storeId, cancellationToken))
        {
            return true;
        }

        // Whether anyone is listed to settle this corridor. Not whether this particular amount
        // clears — a card states bounds per side and which side applies depends on the direction of
        // the trade, so checking one here would refuse sizes a solver would take and admit sizes it
        // would not. Offering a method nobody serves is the failure worth preventing; an amount the
        // chosen solver declines is refused at quoting time, with its reason.
        return await _solver.HasSolverAsync(cancellationToken);
    }

    /// <summary>Evicts a store's cached connection-string verdict.</summary>
    public void ClearStoreCache(string storeId) => _memoryCache.Remove(GetStoreCacheKey(storeId));

    public void Dispose() => _leases.Dispose();

    private async Task<bool> IsStoreUsingArkadeLightningAsync(
        string storeId, CancellationToken cancellationToken)
    {
        var key = GetStoreCacheKey(storeId);
        if (_memoryCache.TryGetValue<bool>(key, out var cached))
        {
            return cached;
        }

        // StoreRepository.FindStore takes no token, so this is where cancellation can still be
        // honoured: on a cache miss, before committing to the database round-trip.
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately not IMemoryCache.GetOrCreateAsync: its factory runs under no lock either, so
        // it buys nothing here, and the shape that reads naturally around an await is this one. Two
        // concurrent misses both load the store and agree on the answer.
        var store = await _storeRepository.FindStore(storeId);
        var usesArkade = store is not null && UsesArkadeLightning(store);

        _memoryCache.Set(key, usesArkade, CacheExpiry);
        return usesArkade;
    }

    private bool UsesArkadeLightning(StoreData store)
    {
        var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lnPaymentMethodId,
            _paymentMethodHandlerDictionary);

        return lnConfig?.ConnectionString?.StartsWith(
            "type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
    }
}
