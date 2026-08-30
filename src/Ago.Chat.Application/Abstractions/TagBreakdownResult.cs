namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-11`: the answer to "what are these conversations actually about" for one site over one
/// caller-supplied window - the site-wide tagging coverage (the honesty check
/// <see cref="ITagBreakdownReadStore"/>'s own remarks name as this item's whole reason to exist) plus a
/// bucket per tag. A plain projection, not an aggregate - nothing here is loaded through
/// <see cref="Domain.Conversation"/> or <see cref="Domain.Tag"/>, the same "read store returns rows, not
/// aggregates" shape every sibling result in this namespace already establishes.
/// </summary>
/// <param name="TotalConversationCount">Every conversation in the window, regardless of tagging - the
/// denominator <see cref="PercentageTagged"/> is computed over.</param>
/// <param name="TaggedConversationCount">Conversations in the window carrying at least one tag, by any
/// <see cref="Domain.TagSource"/> - counted once per conversation even if it holds several tags, unlike
/// <see cref="TagBreakdownBucket.ConversationCount"/> below, which counts once per tag
/// (<see cref="ITagBreakdownReadStore"/>'s own remarks on why the two counting rules are deliberately
/// different).</param>
/// <param name="PercentageTagged"><see langword="null"/> when <see cref="TotalConversationCount"/> is
/// zero - never zero itself, the same "nothing to compute a rate from yet" rule
/// <see cref="ConversionBucket.ConversionRate"/> already applies. Otherwise
/// <see cref="TaggedConversationCount"/> / <see cref="TotalConversationCount"/>.</param>
/// <param name="ByTag">One entry per tag that tagged at least one conversation in the window - never a
/// zero-filled row for a tag nobody used, the same "no manufactured row" rule
/// <see cref="OperatorAnalyticsResult.ByChannel"/> already holds.</param>
public sealed record TagBreakdownResult(
    long TotalConversationCount,
    long TaggedConversationCount,
    double? PercentageTagged,
    IReadOnlyList<TagBreakdownBucket> ByTag);

/// <summary>
/// One tag's own bucket: how many conversations it was applied to in the window (once per tag a
/// conversation holds - <see cref="ITagBreakdownReadStore"/>'s own remarks), and, now that `18-10` has
/// landed, the same conversion-rate shape <see cref="ConversionBucket"/> already establishes, computed
/// only over this tag's own conversations.
/// </summary>
/// <param name="TagId">The tag's own identity - stable across a <see cref="Domain.Tag.Rename"/>, so a
/// caller charting this over time is not broken by an operator relabelling a tag mid-series.</param>
/// <param name="TagName">The tag's current display name - read fresh from <see cref="Domain.Tag"/> each
/// time this report runs, so a rename shows up immediately, the same way <see cref="Domain.Tag.Rename"/>'s
/// own remarks describe for every other reader of a tag's name.</param>
/// <param name="ConversationCount">Conversations in the window carrying this tag - counted once per tag,
/// so a conversation holding two tags contributes to both buckets in full, not split between them.</param>
/// <param name="ConvertedCount">This tag's own conversations recorded as <c>Converted</c>.</param>
/// <param name="NotConvertedCount">This tag's own conversations recorded as <c>NotConverted</c>.</param>
/// <param name="RecordedCount"><see cref="ConvertedCount"/> + <see cref="NotConvertedCount"/> - the exact
/// denominator <see cref="ConversionRate"/> is computed over, the identical shape
/// <see cref="ConversionBucket.RecordedCount"/> already establishes.</param>
/// <param name="ConversionRate"><see langword="null"/> when <see cref="RecordedCount"/> is zero - never
/// zero itself, and never inflated or deflated by a <c>FollowUpNeeded</c> or unrecorded outcome (both
/// excluded from the denominator, the identical `18-10` decision <see cref="ConversionBucket.ConversionRate"/>'s
/// own remarks state).</param>
public sealed record TagBreakdownBucket(
    Domain.TagId TagId,
    string TagName,
    long ConversationCount,
    long ConvertedCount,
    long NotConvertedCount,
    long RecordedCount,
    double? ConversionRate);
