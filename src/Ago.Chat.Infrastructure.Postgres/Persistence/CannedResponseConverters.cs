using System.Text.Json;
using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `18-03`: how a site's canned responses map onto one column - the same shape
/// <see cref="OfflineAutoReplyConverters"/> already established for its own sibling list, restated
/// here rather than shared, because the two lists hold different domain types
/// (<see cref="Ago.Chat.Domain.CannedResponse"/> is not <see cref="OfflineAutoReplyRule"/> - see that
/// type's own remarks on why) even though the storage reasoning is identical.
///
/// <para><b>Why one <c>text</c> column and not a child table.</b> Read as a unit, written as a unit,
/// never queried into - the picker fetches the whole list and browses it client-side; nothing will
/// ever ask "which sites have a canned response containing 'refund'." <see cref="OfflineAutoReplyConverters"/>'s
/// own remarks apply verbatim.</para>
///
/// <para>Why <c>text</c> and not <c>jsonb</c>: the same answer, for the same reason - nothing queries
/// into this column, and <c>jsonb</c> exists to be queried into.</para>
/// </summary>
internal static class CannedResponseConverters
{
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<List<CannedResponse>?, string?> Responses = new(
        responses => responses == null || responses.Count == 0
            ? null
            : JsonSerializer.Serialize(responses.Select(r => new StoredCannedResponse(r.Title, r.Body)), StorageOptions),
        value => value == null
            ? null
            : JsonSerializer.Deserialize<List<StoredCannedResponse>>(value, StorageOptions)!
                .Select(r => new CannedResponse(r.Title, r.Body))
                .ToList());

    /// <summary>EF needs one for any collection behind a value converter - <see cref="OfflineAutoReplyConverters.RulesComparer"/>'s
    /// own remarks on why this is genuinely load-bearing, not defensive boilerplate, apply verbatim:
    /// <see cref="Site"/> is updated in place by <c>UpdateCannedResponsesHandler</c>.</summary>
    public static readonly ValueComparer<List<CannedResponse>?> ResponsesComparer = new(
        (left, right) => left == null
            ? right == null
            : right != null && left.SequenceEqual(right),
        responses => responses == null
            ? 0
            : responses.Aggregate(0, (hash, response) => HashCode.Combine(hash, response.Title, response.Body)),
        responses => responses == null ? null : responses.ToList());

    /// <summary>The stored shape, separate from <see cref="CannedResponse"/> so the domain type stays
    /// free of serialisation concerns - <see cref="OfflineAutoReplyConverters.StoredRule"/>'s own
    /// precedent.</summary>
    private sealed record StoredCannedResponse(string Title, string Body);
}
