using System.Diagnostics.CodeAnalysis;

// One assembly-wide suppression, and it records an architectural decision rather than waiving a finding.
//
// SonarCloud reports S6640 once per `unsafe` declaration - 23 of them across ten files in this project, and
// none anywhere else in the solution. That count is a restatement of the architecture, not a list of
// defects: CLAUDE.md requires every P/Invoke to live in AppLedger.Infrastructure, `AllowUnsafeBlocks` is
// enabled on this project alone, and the blocks are pointer walks over Win32 structures that have no safe
// equivalent - SYSTEM_PROCESS_INFORMATION at a variable stride, MIB_*_TABLE rows, PDH counter arrays,
// SECURITY_DESCRIPTOR and WINTRUST_DATA blobs. The offsets they depend on are asserted by tests precisely
// because reading a table at the wrong stride yields plausible garbage rather than an error
// (docs/11_SAFETY_POLICY.md, docs/17_BUILD.md §CsWin32).
//
// Leaving them reported is not the cautious option. Twenty-three findings that are one decision would bury
// the next real security finding in the same list, which is the opposite of what the dashboard is for. The
// safety claim this product actually makes - no injection, no VM_READ, no handle enumeration, handles opened
// with PROCESS_QUERY_LIMITED_INFORMATION only - is enforced by BannedSymbols.txt at build time and by
// PolicyGuard at run time, neither of which this suppression touches.
//
// Narrowing it is the standing improvement: one `unsafe` disappears the moment CsWin32 0.3.367 lands, where
// SHGetKnownFolderPath yields a SafeHandle and KnownFolders no longer frees COM memory by hand
// (docs/24_ADR.md §Findings).
[assembly: SuppressMessage(
    "Security",
    "S6640:Unsafe code should not be used",
    Scope = "module",
    Justification = "Every unsafe block here is a reviewed Win32 pointer walk in the one project allowed "
        + "to hold P/Invoke (CLAUDE.md), with no safe equivalent and with its offsets asserted by tests. "
        + "The product's safety claims are enforced by BannedSymbols.txt and PolicyGuard, not by this rule.")]
