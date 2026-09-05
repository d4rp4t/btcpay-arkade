using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment prompt details for Ark payments.
/// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
/// </summary>
public record ArkadePromptDetails
{
    /// <summary>
    /// Creates prompt details from a wallet ID and contract.
    /// </summary>
    public ArkadePromptDetails(string walletId, ArkContract contract)
        : this(walletId, contract.ToString())
    {
    }

    /// <summary>
    /// Payment prompt details for Ark payments.
    /// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
    /// </summary>
    public ArkadePromptDetails(string WalletId,
        string ContractString)
    {
        this.WalletId = WalletId;
        this.ContractString = ContractString;
    }
    
    public ArkadePromptDetails()
    {
        
    }

    public string WalletId { get; init; }
    public string ContractString { get; init; }
    public string? BoardingAddress { get; init; }
    public string? BoardingContractString { get; init; }

    /// <summary>The L1 HTLC a swap payer funds, when the fast onchain path was negotiated.</summary>
    /// <remarks>
    /// Present instead of a swap, never alongside a second onchain option: the two want different
    /// amounts at the same address family and offering both would invite the payer to split.
    /// </remarks>
    public string? SwapHtlcAddress { get; init; }

    /// <summary>
    /// What the swap's HTLC must receive, in sats — <b>exactly this, in exactly one output</b>.
    /// </summary>
    /// <remarks>
    /// Not the invoice's own due amount, and not a minimum. The solver matches the address for a
    /// single output equal to the quote: underfunding cannot be topped up, because a second payment
    /// is a second output and its claim spends one input; overfunding is not taken at all, and comes
    /// back through the L1 refund. Anything but this number turns a payment into a wait for that
    /// refund, so it is what the BIP21 URI must carry.
    /// </remarks>
    public long? SwapFundAmountSats { get; init; }

    /// <summary>The negotiation id, so the advance loop's swap can be found from the invoice.</summary>
    public string? SwapId { get; init; }

    /// <summary>
    /// Parses the contract with the specified network.
    /// </summary>
    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContractParser.Parse(ContractString, network);
    }

}
