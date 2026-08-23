using AppLedger.Core.Catalog;

namespace AppLedger.Core.Net;

/// <summary>
/// Applies the per-app host-logging policy to a host name, both at rollup time and on the live stream.
/// </summary>
/// <remarks>
/// This is the single point where "six months of per-app host names" is turned into what the user
/// actually agreed to. The live path applies the same rules as the stored path on purpose: the UI never
/// receives data the policy says it should not display (docs/07_IPC.md §Payload rules, docs/12 §Defaults).
/// </remarks>
public sealed class HostPolicy
{
    /// <summary>The bucket a host falls into when the policy is <see cref="HostLogging.None"/>.</summary>
    public const string HiddenBucket = "(hidden)";

    /// <summary>The bucket for a remote address that no DNS answer ever named.</summary>
    public const string UnnamedBucket = "(ip)";

    /// <summary>The bucket everything beyond the per-day cap collapses into.</summary>
    public const string OverflowBucket = "(other)";

    /// <summary>Default hosts stored per app per local day before overflow (docs/12 §Defaults).</summary>
    public const int DefaultHostsPerAppPerDay = 200;

    private readonly PublicSuffixList _suffixes;

    /// <summary>Creates a policy over a loaded public suffix list.</summary>
    public HostPolicy(PublicSuffixList suffixes)
    {
        ArgumentNullException.ThrowIfNull(suffixes);
        _suffixes = suffixes;
    }

    /// <summary>
    /// The default level for a category. Browser and System store byte totals only; everything else is
    /// reduced to the registrable domain. Users may relax this per app; we never do it for them.
    /// </summary>
    public static HostLogging DefaultForCategory(string? category) => category switch
    {
        "Browser" => HostLogging.None,
        "System" => HostLogging.None,
        _ => HostLogging.Etld1,
    };

    /// <summary>
    /// Shapes a host name for storage or display under the given level. Returns
    /// <see cref="HiddenBucket"/> for <see cref="HostLogging.None"/>, the registrable domain for
    /// <see cref="HostLogging.Etld1"/>, and the full name for <see cref="HostLogging.Full"/>.
    /// </summary>
    /// <param name="host">The observed host name, or null when only an address is known.</param>
    /// <param name="level">The app's effective host-logging level.</param>
    public string Shape(string? host, HostLogging level)
    {
        if (level == HostLogging.None)
        {
            return HiddenBucket;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return UnnamedBucket;
        }

        if (PublicSuffixList.IsIpLiteral(host))
        {
            return UnnamedBucket;
        }

        if (level == HostLogging.Full)
        {
            return host.Trim().TrimEnd('.').ToLowerInvariant();
        }

        return _suffixes.GetRegistrableDomain(host) ?? UnnamedBucket;
    }

    /// <summary>
    /// True when a shaped value is one of the aggregate buckets rather than a real host, so callers do not
    /// try to expand "(hidden)" with a DNS lookup.
    /// </summary>
    public static bool IsBucket(string? shaped) =>
        shaped is HiddenBucket or UnnamedBucket or OverflowBucket;

    /// <summary>
    /// Groups an IPv4 address into its /24 and an IPv6 address into its /48, the cardinality bound
    /// docs/10 §Host policy puts on the unnamed bucket. Returns null when the value is not an address.
    /// </summary>
    public static string? ToAddressPrefix(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !PublicSuffixList.IsIpLiteral(address))
        {
            return null;
        }

        if (address.Contains(':', StringComparison.Ordinal))
        {
            var groups = address.Split(':');
            var kept = new List<string>(3);
            foreach (var group in groups)
            {
                if (kept.Count == 3)
                {
                    break;
                }

                kept.Add(group.Length == 0 ? "0" : group);
            }

            return string.Join(':', kept) + "::/48";
        }

        var octets = address.Split('.');
        return string.Join('.', octets[0], octets[1], octets[2], "0") + "/24";
    }
}
