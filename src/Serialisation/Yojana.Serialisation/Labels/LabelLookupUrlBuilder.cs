using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Labels;

/// <summary>
/// Builds the serial lookup target without coupling label rendering to an HTTP request. A deployment may use a
/// <c>{serial}</c> route template; otherwise the serial is appended as the <c>q</c> query parameter.
/// </summary>
public sealed class LabelLookupUrlBuilder(string lookupUrl) : ILabelLookupUrlBuilder
{
    private readonly string lookupUrl = string.IsNullOrWhiteSpace(lookupUrl)
        ? throw new ArgumentException("A label lookup URL is required.", nameof(lookupUrl))
        : lookupUrl.Trim();

    public string ForSerial(string serial)
    {
        var encoded = Uri.EscapeDataString(serial);
        if (lookupUrl.Contains("{serial}", StringComparison.Ordinal))
            return lookupUrl.Replace("{serial}", encoded, StringComparison.Ordinal);
        return lookupUrl + (lookupUrl.Contains('?', StringComparison.Ordinal) ? "&q=" : "?q=") + encoded;
    }
}
