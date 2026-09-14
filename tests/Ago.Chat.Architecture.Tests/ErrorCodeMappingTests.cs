namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `25-98`'s own answer to its second Done-when box: is an enforcing test worth building now that the
/// audit itself is honest? <b>Yes, and this is it</b> - but a narrower claim than the naive version
/// this item's own text explicitly rejected ("enumerate every `*Errors` factory method, assert none
/// falls through to `500`") ever could have supported.
///
/// <para><b>What changed between "no" and "yes".</b> The naive version had no independent source for
/// "and here is the status each one *should* have" - built against the switch it was testing, it could
/// only ever assert "not 500", which cannot tell a deliberate, correct `500` (`demo.unavailable`) apart
/// from a still-undiscovered gap, and would have had to either hard-code today's exclusion list
/// (freezing debt as permanent) or fail on every already-known gap on day one (indistinguishable from a
/// new regression). This item's own audit is that independent source now: every code below is either
/// mapped to a status this item's own report derived from the code's own doc comment and its actual
/// callers (not from what the switch happened to already say), or named in
/// <see cref="ErrorCodeMappingExemptions"/> with a checked, specific reason. What this test enforces is
/// narrower and sound where the naive version was not: not "is the status correct" (still a human
/// judgement call, made once per code, the same way every other line in `ErrorExtensions.cs` was
/// decided) but <b>"has someone decided, on purpose, and written it down"</b> - which is exactly the
/// property this item's own audit needed and the codebase had never had before it.</para>
///
/// <para><b>What this test would have caught, mechanically, before this item existed.</b> All 33 codes
/// this item's own report fixes, and the two `25-91` fixed before it (`Attachment.DownloadBlocked`,
/// `Site.DownloadBlockExemptionReasonRequired`) - every one of them a code a real endpoint could
/// return, silently landing on `500`, for anywhere from one item to several stages. A new `*Errors`
/// factory method added after this test exists and never given a line in `ErrorExtensions.cs` (nor a
/// reasoned exemption) now fails the build instead of shipping quietly.</para>
/// </summary>
public sealed class ErrorCodeMappingTests
{
    [Fact]
    public void EveryErrorCode_IsMappedInErrorExtensions_OrExplicitlyExempt()
    {
        var source = ReadErrorExtensionsSource();
        var unclassified = ErrorCodeCatalog.FromApplicationAssembly()
            .Where(site => !IsMapped(source, site.Code) && !ErrorCodeMappingExemptions.IsExempt(site.Code))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "These error codes reach a real Result<T> failure but ErrorExtensions.ToProblem's switch gives "
            + "them no line, and ErrorCodeMappingExemptions gives no reason either - both silently fall "
            + "through to the `500` default, turning an ordinary refusal into a fault "
            + "(docs/backlog/25-98-*.md's own point). Either add a case to the switch (if this code can reach "
            + "a real HTTP endpoint) or a reasoned entry to ErrorCodeMappingExemptions (if it genuinely "
            + "cannot - e.g. a Hub-only code that only ever reaches HubException). Found: "
            + string.Join("; ", unclassified));
    }

    /// <summary>The other direction, and the one that keeps the exemption list honest - the same
    /// staleness check <see cref="MessageOpacityTests.NoExemption_IsStale"/> and
    /// <see cref="TenantScopeTests.NoExemption_IsStale"/> already make for their own rules. An
    /// exemption for a code that has since been mapped, renamed, or had its factory method deleted is
    /// a claim sitting in a file whose entire value is that a reviewer can trust what it says.</summary>
    [Fact]
    public void NoExemption_IsStale()
    {
        var liveCodes = ErrorCodeCatalog.FromApplicationAssembly()
            .Select(site => site.Code)
            .ToHashSet(StringComparer.Ordinal);

        var stale = ErrorCodeMappingExemptions.ByCode.Keys
            .Where(code => !liveCodes.Contains(code))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "Stale entries in ErrorCodeMappingExemptions - the code they excuse no longer exists (renamed, "
            + "removed, or already mapped): " + string.Join("; ", stale));
    }

    /// <summary>
    /// <b>The rule, proven able to fail</b> - the same permanent-fixture-free technique this file's own
    /// two sibling rules use a real fixture assembly for (<see cref="MessageOpacityTests.TheRule_FlagsAMessageModelThatGrewBookingShapedFields"/>,
    /// <see cref="TenantScopeTests.TheRule_FlagsAHandlerThatTakesASiteIdAndNeverChecksPermission"/>); this
    /// rule's own matching logic is a single string search rather than an IL scan, so a synthetic
    /// source string exercises it exactly as faithfully as a compiled fixture would, with no second
    /// assembly to maintain.
    /// </summary>
    [Fact]
    public void TheRule_FlagsACodeNamedInNeitherTheSwitchNorTheExemptions()
    {
        const string sourceWithOneMappedCode = "\"Conversation.NotFound\" => StatusCodes.Status404NotFound,";

        Assert.True(IsMapped(sourceWithOneMappedCode, "Conversation.NotFound"));
        Assert.False(IsMapped(sourceWithOneMappedCode, "Conversation.SomeNewCode"));
    }

    private static bool IsMapped(string errorExtensionsSource, string code) =>
        errorExtensionsSource.Contains($"\"{code}\"", StringComparison.Ordinal);

    private static string ReadErrorExtensionsSource()
    {
        var path = Path.Combine(SourceTreeLocator.FindSrcDirectory(), "Ago.Chat.Api", "Http", "ErrorExtensions.cs");
        return File.ReadAllText(path);
    }
}
