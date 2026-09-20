using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ArkadeLightningAvailabilityService _availability;

    public ArkadePaymentLinkExtension(
        IServiceProvider serviceProvider,
        ArkadeLightningAvailabilityService availability)
    {
        _serviceProvider = serviceProvider;
        _availability = availability;
    }
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // Get other payment methods if available
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        var amount = prompt.Calculate().Due;

        // Build BIP21 URI using the helper
        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);

        // Add onchain address if available, otherwise use boarding address.
        // When an onchain payment method is present, also delegate to its own
        // IPaymentLinkExtension so any params other plugins attached to the
        // upstream BIP21 (PayJoin's `pj=`, Branta's `branta_*`, etc.) carry
        // through to the unified Arkade QR. Without this the Arkade tab
        // clobbers those params (issue: Branta + PayJoin lose their hooks).
        if (!string.IsNullOrEmpty(onchain?.Destination))
        {
            builder.WithOnchainAddress(onchain.Destination);

            var onchainLink = _serviceProvider.GetServices<IPaymentLinkExtension>()
                .FirstOrDefault(p => p.PaymentMethodId == onchain.PaymentMethodId);
            if (onchainLink is not null)
            {
                var upstream = onchainLink.GetPaymentLink(onchain, urlHelper);
                var qIdx = upstream?.IndexOf('?') ?? -1;
                if (qIdx >= 0)
                    builder.WithExtraQuery(upstream![(qIdx + 1)..]);
            }
        }
        else if (prompt.Details is not null)
        {
            var handler = _serviceProvider.GetRequiredService<ArkadePaymentMethodHandler>();
            var details = handler.ParsePaymentPromptDetails(prompt.Details);

            if (!string.IsNullOrEmpty(details.SwapHtlcAddress))
            {
                builder.WithOnchainAddress(details.SwapHtlcAddress);

                // The solver's number, not the invoice's, even though the two normally agree: the
                // quote is asked for on the side the payer funds, so what it answers with IS what
                // the HTLC must receive. Reading the invoice's own due amount instead would be
                // relying on that agreement holding, and this corridor gives no second chance —
                // one output, exact value, or the swap is dead and the money waits for a refund.
                if (details.SwapFundAmountSats is { } exact)
                {
                    builder.WithAmount(Money.Satoshis(exact).ToUnit(MoneyUnit.BTC));
                }
            }
            else if (!string.IsNullOrEmpty(details.BoardingAddress))
            {
                builder.WithOnchainAddress(details.BoardingAddress);
            }
        }
        
        // Add the Lightning invoice when there is one (preferred over LNURL).
        if (ShouldIncludeLightning(prompt).Result)
        {
            builder.WithLightning(ln.Destination);
        }
        else if (lnurl is not null && _serviceProvider.GetServices<IPaymentLinkExtension>()
                     .FirstOrDefault(p => p.PaymentMethodId == lnurl.PaymentMethodId) is { } lnurlLink)
        {
            if (lnurlLink.GetPaymentLink(lnurl, urlHelper) is { } link)
            {
                builder.WithLightning(link.Replace("lightning:", string.Empty));
            }
        }

        return builder.Build();
    }

    private async Task<bool> ShouldIncludeLightning(PaymentPrompt prompt)
    {
        return await _availability.ShouldOfferLightningAsync(
            prompt.ParentEntity.StoreId, CancellationToken.None);
    }
}
