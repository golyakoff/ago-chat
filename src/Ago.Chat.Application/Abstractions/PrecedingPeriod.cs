namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-16`: "a figure carries the preceding period to compare it against" (`docs/design/decisions.md`
/// §7) - one shared computation, not four restatements, because unlike a UX default
/// (<c>GetOperatorAnalyticsForSiteHandler.DefaultWindowDays</c> and its three siblings, each restated
/// rather than shared - "no cross-use-case constant" is the deliberate precedent those classes' own
/// remarks give) this is arithmetic that must produce the identical answer everywhere it runs. A
/// constant is safe to duplicate - four copies of `30` cannot drift apart from each other in a way that
/// matters. A *computation* can: if `GetTagBreakdownReportForSiteHandler` ever wrote
/// <c>from.AddDays(-(to - from).Days)</c> instead of subtracting the exact <see cref="TimeSpan"/>, a
/// window measured in hours (not whole days) would silently compare against the wrong length, and
/// nothing would catch it until the numbers looked wrong on a report nobody had reason to hand-check.
/// One method, one place a reader (or a test) can trust every caller means the same thing by "preceding
/// period".
///
/// <para><b>Computed server-side, in one place, per the item's own scope, and what "in one place"
/// buys.</b> The alternative was letting the console call the same report endpoint twice (once for the
/// current window, once for a caller-computed previous one) - rejected because the window rule is the
/// honesty rule (`docs/backlog/23-16-*.md`'s own Scope section states this explicitly): a browser that
/// gets to choose what counts as "the preceding period of equal length" could just as easily choose a
/// flattering one, and every future surface reading the same report would have to reimplement the exact
/// same arithmetic correctly or silently disagree with this one. Each handler calls its own read store
/// twice instead (see e.g. <c>GetConversionReportForSiteHandler</c>) and this method is the one place
/// that decides what "twice" means.</para>
///
/// <para><b>Zone-agnostic instant arithmetic, stated precisely because rule 11 requires it to be.</b>
/// <paramref name="from"/>/<paramref name="to"/> arrive already resolved to absolute instants - either
/// the console's own local-day-boundary resolution (`ConversionReportPage.startOfDayIso`/`endOfDayIso`
/// and its three siblings turn a `&lt;input type="date"&gt;` value into a concrete instant in the
/// browser's own zone before the request is ever sent), or UTC when the caller supplied neither and the
/// handler defaulted the window from <c>IClock</c>. Subtracting a <see cref="TimeSpan"/> from a
/// <see cref="DateTimeOffset"/> does not re-resolve either bound against any zone - the preceding window
/// inherits whichever one produced <paramref name="from"/>/<paramref name="to"/>, unchanged. The
/// consequence worth stating rather than silently living with: "equal length" here means equal *elapsed
/// duration*, not equal count of local-calendar days, so a window that straddles a DST transition
/// compares against a preceding window that differs from it by that transition's hour when the two are
/// read back in local wall-clock terms. That is what the item's own words ask for ("equal length"), not
/// an error to work around.</para>
/// </summary>
public static class PrecedingPeriod
{
    /// <summary>The half-open window of equal length immediately before <paramref name="from"/>:
    /// <c>[from - (to - from), from)</c> - <c>to</c> the caller passes in is only used for its length,
    /// the same half-open convention (inclusive start, exclusive end) every window in this codebase
    /// already uses, so the preceding window's own end lines up exactly on the current window's own
    /// start with no gap and no overlap.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Before(DateTimeOffset from, DateTimeOffset to)
    {
        var length = to - from;
        return (from - length, from);
    }
}
