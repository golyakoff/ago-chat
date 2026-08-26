using System.Text.Json;
using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `14-04`: how a site's keyword rules map onto one column.
///
/// <para><b>Why one <c>text</c> column and not a <c>site_auto_reply_rules</c> child table.</b> The
/// rules are read as a unit, written as a unit, and never queried into - nothing will ever ask "which
/// sites have a rule containing 'refund'", because the only reader is the matcher, which needs all of
/// them in order anyway. A child table would buy that query capability and charge a join on a path
/// whose whole point is being cheap, plus a second aggregate boundary inside <see cref="Site"/> for a
/// list capped at twenty entries.</para>
///
/// <para><b>Why <c>text</c> and not <c>jsonb</c>.</b> The same question `14-06` answered for
/// <c>messages.actions</c> decides it, and the same way: <c>jsonb</c> exists to be queried into, and
/// nothing queries into this. See <see cref="MessageContentConverters"/>'s own remarks - this file
/// follows that precedent rather than inventing a second storage convention for the same kind of
/// value.</para>
///
/// <para>An empty list and <see langword="null"/> both store as <c>NULL</c>: "this site has no keyword
/// rules" has one meaning, and two encodings of one meaning is how a query starts having to check for
/// both.</para>
/// </summary>
internal static class OfflineAutoReplyConverters
{
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<List<OfflineAutoReplyRule>?, string?> Rules = new(
        rules => rules == null || rules.Count == 0
            ? null
            : JsonSerializer.Serialize(rules.Select(r => new StoredRule(r.Keyword, r.Reply)), StorageOptions),
        value => value == null
            ? null
            : JsonSerializer.Deserialize<List<StoredRule>>(value, StorageOptions)!
                .Select(r => new OfflineAutoReplyRule(r.Keyword, r.Reply))
                .ToList());

    /// <summary>
    /// EF needs one for any collection behind a value converter, or it cannot tell whether the
    /// property changed and falls back to reference equality on a list it did not create. Unlike
    /// <see cref="MessageContentConverters.ActionsComparer"/>, this one is genuinely load-bearing:
    /// <see cref="Site"/> is updated in place by <c>UpdateOfflineAutoReplyHandler</c>, so a missed
    /// change here would be a silently discarded save.
    /// </summary>
    public static readonly ValueComparer<List<OfflineAutoReplyRule>?> RulesComparer = new(
        (left, right) => left == null
            ? right == null
            : right != null && left.SequenceEqual(right),
        rules => rules == null
            ? 0
            : rules.Aggregate(0, (hash, rule) => HashCode.Combine(hash, rule.Keyword, rule.Reply)),
        rules => rules == null ? null : rules.ToList());

    /// <summary>The stored shape, separate from <see cref="OfflineAutoReplyRule"/> so the domain type
    /// stays free of serialisation concerns and a column-format change never becomes a domain
    /// change - <see cref="MessageContentConverters"/>'s own <c>StoredAction</c> precedent.</summary>
    private sealed record StoredRule(string Keyword, string Reply);
}
