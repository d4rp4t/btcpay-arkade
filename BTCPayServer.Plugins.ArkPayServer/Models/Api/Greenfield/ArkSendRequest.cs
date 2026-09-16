namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to send Ark funds to a destination.
/// </summary>
public class ArkSendRequest
{
    /// <summary>
    /// Destination: Arkade address, Bitcoin address, or BIP21 URI.
    /// </summary>
    public string Destination { get; set; } = "";

    /// <summary>
    /// Amount in satoshis. Null means "send all available".
    /// </summary>
    public long? AmountSats { get; set; }

    /// <summary>
    /// Specific VTXO outpoints to use as inputs. If empty, coins are selected automatically.
    /// </summary>
    public List<string>? InputOutpoints { get; set; }
}

/// <summary>
/// Response after submitting a send request.
/// </summary>
public class ArkSendResponse
{
    /// <summary>
    /// The Arkade transaction ID.
    /// </summary>
    public string? TxId { get; set; }
}
