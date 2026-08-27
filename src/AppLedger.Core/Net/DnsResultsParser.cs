using System.Net;

namespace AppLedger.Core.Net;

/// <summary>
/// Parses the <c>QueryResults</c> field of DNS-Client event 3008/3020 (docs/10_NETWORK_AND_DNS.md §DNS).
/// </summary>
/// <remarks>
/// The field has no schema worth the name. It is a <c>;</c>-separated list mixing <c>type: N value</c>
/// pairs with bare addresses, IPv4 sometimes appears as an IPv4-mapped IPv6 literal, and the shape varies
/// between Windows builds. So the parser is deliberately tolerant: it takes what it recognises and ignores
/// the rest, because a single unfamiliar token must not cost us the addresses that came with it.
/// <para>
/// Being pure, it is tested against captured samples rather than against a live resolver — which is what
/// makes DNS attribution verifiable on a machine with no admin rights.
/// </para>
/// </remarks>
public static class DnsResultsParser
{
    /// <summary>The record type number for CNAME, the one non-address answer we care about.</summary>
    private const int CnameType = 5;

    /// <summary>
    /// Extracts every address the answer carried. Returns an empty list for anything unparseable — a DNS
    /// answer we cannot read is a missing label, never an exception on an ETW callback thread.
    /// </summary>
    public static IReadOnlyList<IPAddress> ParseAddresses(string? queryResults)
    {
        if (string.IsNullOrWhiteSpace(queryResults))
        {
            return [];
        }

        List<IPAddress>? addresses = null;

        foreach (var range in queryResults.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = StripTypePrefix(range, out var recordType);

            // A CNAME's value is a name, not an address; the chain itself is only fetched on demand.
            if (recordType == CnameType || token.Length == 0)
            {
                continue;
            }

            if (!IPAddress.TryParse(token, out var address))
            {
                continue;
            }

            // ::ffff:a.b.c.d is the same address as a.b.c.d, and storing both would split one host's
            // traffic across two rows.
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            (addresses ??= []).Add(address);
        }

        return addresses ?? (IReadOnlyList<IPAddress>)[];
    }

    /// <summary>
    /// Extracts the CNAME targets, in the order the answer listed them. Used by the on-demand host expander
    /// rather than by the live path.
    /// </summary>
    public static IReadOnlyList<string> ParseCnames(string? queryResults)
    {
        if (string.IsNullOrWhiteSpace(queryResults))
        {
            return [];
        }

        List<string>? names = null;

        foreach (var range in queryResults.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = StripTypePrefix(range, out var recordType);
            if (recordType == CnameType && token.Length > 0)
            {
                (names ??= []).Add(token.TrimEnd('.'));
            }
        }

        return names ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Removes a leading <c>type: N </c> prefix and reports the type it named, or -1 when there was none.
    /// </summary>
    private static string StripTypePrefix(string entry, out int recordType)
    {
        recordType = -1;

        const string Prefix = "type:";
        if (!entry.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return entry;
        }

        var rest = entry[Prefix.Length..].TrimStart();
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            // "type: 5" with no value: a type and nothing to go with it.
            return string.Empty;
        }

        if (int.TryParse(rest[..space], out var parsed))
        {
            recordType = parsed;
        }

        return rest[(space + 1)..].Trim();
    }
}
