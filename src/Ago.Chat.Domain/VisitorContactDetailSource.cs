namespace Ago.Chat.Domain;

/// <summary>
/// `23-09`: who supplied a <see cref="VisitorContactDetail"/> - an operator who typed down a fact a
/// visitor said out loud (`14-14`'s original and only shape), or the visitor themselves, filling in
/// the widget-native control this item adds for the out-of-hours case
/// (`docs/design/decisions.md` §4). Stored as the CLR member name via EF's default string conversion,
/// the same reasoning <see cref="VisitorContactDetailKind"/>'s own remarks give for itself: an ordinal
/// makes reordering this enum a silent data corruption.
///
/// <para><b>Why this matters enough to be its own column, not inferred from <c>RecordedByOperatorId</c>
/// being null.</b> A null operator id is ambiguous on its own - it could mean "the visitor supplied
/// this" or "a future write path that has nothing to do with either" - and a column a reader has to
/// reason about via absence is exactly what `VisitorContactDetail`'s own remarks on `Kind` (a real
/// column, not inferred from `Value`'s shape) already reject for a sibling field. `Source` says
/// plainly which it is, and `RecordedByOperatorId` being null is a *consequence* of
/// <see cref="Visitor"/> rather than the fact itself.</para>
/// </summary>
public enum VisitorContactDetailSource
{
    Operator,
    Visitor,
}
