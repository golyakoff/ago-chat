using System.Text.Json;
using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// How `14-06`'s three columns map, and the storage decision behind them.
///
/// <para><b>Why <c>text</c> and not <c>jsonb</c>, for the payload.</b> The question that decides it
/// is the one <c>data-model.md</c> now records: does anything ever need to query <i>into</i> the
/// payload? The answer is <b>no, by design and permanently</b> - AGO Chat never interprets it, so it
/// can never filter, group or index on its contents, and the day it could would be the day the
/// boundary this whole item protects had already been crossed. Everything <c>jsonb</c> buys
/// (<c>-&gt;</c>, <c>@&gt;</c>, GIN) is that capability; paying a parse and a binary re-encode on
/// every insert into the largest, partitioned table in the system to buy a capability whose use
/// would be an architecture violation is the wrong trade.</para>
///
/// <para>Two consequences follow, both of which happen to be what this field wants. <c>text</c>
/// round-trips the producer's own bytes verbatim, so a payload a product signed or hashed still
/// verifies - <c>jsonb</c> reorders keys and drops duplicates, which is invisible until it is not.
/// And a payload over roughly 2 KB goes to TOAST, compressed and out of line, so a large one does
/// not widen the main heap that the hot keyset-by-sequence read scans.</para>
///
/// <para><b>Why three nullable columns rather than one composite.</b> On a prose message - which is
/// every message today - three NULLs cost <b>zero additional bytes</b>: Postgres records them in the
/// row's null bitmap, which is sized in bytes and already exists for
/// <c>attachment_id</c>/<c>client_message_id</c>. <c>messages</c> goes from 9 mapped columns to 12,
/// and 9 and 12 both round to the same two bytes of bitmap. So the composite alternative would have
/// bought nothing measurable and cost readability in <c>psql</c> and a converter around the whole
/// aggregate.</para>
///
/// <para><b>The actions column is the one AGO Chat does read.</b> That asymmetry is deliberate and is
/// the point of <see cref="MessageAction"/>: AGO Chat owns the actions' schema (a label and an opaque
/// value) and must be able to enumerate them for a channel with no UI; it owns no schema for the
/// payload and never looks inside. Serialised as a JSON array in one <c>text</c> column rather than a
/// child table because <c>messages</c> is <c>PARTITION BY RANGE</c> and a child table would need
/// either its own partitioning or a foreign key pointing at a partitioned parent - the same reason
/// <c>attachments.message_id</c> carries no FK (<c>data-model.md</c>) - and because the hot read
/// would grow a join for a list that is empty on virtually every row.</para>
/// </summary>
internal static class MessageContentConverters
{
    /// <summary>Compact, no indentation: this is a storage format nobody reads by eye, and the
    /// column is on the biggest table in the system.</summary>
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<MessageContentKind?, string?> Kind = new(
        kind => kind.HasValue ? kind.Value.Value : null,
        value => value == null ? null : new MessageContentKind(value));

    public static readonly ValueConverter<MessagePayload?, string?> Payload = new(
        payload => payload.HasValue ? payload.Value.Value : null,
        // Back through the constructor, so a row that somehow holds something that is not a JSON
        // object fails loudly at materialisation instead of flowing on to a client that cannot draw
        // it - the same asymmetric round trip PhoneNumber's converter uses in ago-calendar.
        value => value == null ? null : new MessagePayload(value));

    /// <summary>
    /// The actions list, as a JSON array of <c>{label, value}</c>.
    ///
    /// <para>An empty list and <see langword="null"/> are both stored as <c>NULL</c>: "this message
    /// offers no choices" has one meaning, and two encodings of one meaning is how a query starts
    /// needing to check for both.</para>
    /// </summary>
    public static readonly ValueConverter<List<MessageAction>?, string?> Actions = new(
        actions => actions == null || actions.Count == 0
            ? null
            : JsonSerializer.Serialize(actions.Select(a => new StoredAction(a.Label, a.Value)), StorageOptions),
        value => value == null
            ? null
            : JsonSerializer.Deserialize<List<StoredAction>>(value, StorageOptions)!
                .Select(a => new MessageAction(a.Label, a.Value))
                .ToList());

    /// <summary>
    /// EF needs one for any collection behind a value converter, or it cannot tell whether the
    /// property changed and falls back to reference equality on a list it did not create.
    ///
    /// <para>A message is immutable and never updated after its insert, so nothing here would ever
    /// notice a missed change - which is exactly why it is written explicitly rather than left to a
    /// default that happens to be harmless today. Deep by value, and it produces a genuine copy on
    /// snapshot, so the change tracker's copy cannot alias the list the aggregate holds.</para>
    /// </summary>
    public static readonly ValueComparer<List<MessageAction>?> ActionsComparer = new(
        (left, right) => left == null
            ? right == null
            : right != null && left.SequenceEqual(right),
        actions => actions == null
            ? 0
            : actions.Aggregate(0, (hash, action) => HashCode.Combine(hash, action.Label, action.Value)),
        actions => actions == null ? null : actions.ToList());

    /// <summary>The stored shape, separate from <see cref="MessageAction"/> so that the domain type
    /// stays free of serialisation concerns and a column format change never becomes a domain
    /// change.</summary>
    private sealed record StoredAction(string Label, string Value);
}
