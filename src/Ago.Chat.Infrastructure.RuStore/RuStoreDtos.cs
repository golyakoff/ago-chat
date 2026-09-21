using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.RuStore;

/// <summary>
/// `26-04`: RuStore's own send-API request shape, read from `adr/0180` §1/§3 and the send API's own
/// reference - `{"message": {...}}`, `message.token`, `message.data`. Deliberately no
/// <c>notification</c> and no top-level <c>android</c> priority/collapse fields: `adr/0179` §3's
/// client-owns-loudness design only works for a message the RuStore SDK does not render itself, and
/// RuStore has neither a <c>priority</c> nor a <c>collapse_key</c> field to set even if this design
/// wanted one (`adr/0180` §4).
/// </summary>
internal sealed record RuStoreSendRequest(
    [property: JsonPropertyName("message")] RuStoreMessage Message);

/// <summary>
/// <see cref="Data"/> is the whole payload - see <see cref="RuStorePushSender.BuildData"/> for how
/// <c>PushMessage.Title</c>/<c>Body</c>/<c>GroupKey</c> and its own <c>Data</c> map are folded into this
/// one flat <c>map[string]string</c>, and that method's own remarks for why this reads it as a flat map
/// rather than a nested <c>{"payload": {...}}</c> wrapper - the one ambiguity `26-04`'s own backlog item
/// names as unresolvable from documentation alone.
///
/// <para><see cref="Android"/> is never <see langword="null"/> - <see cref="RuStoreAndroidConfig.Ttl"/>
/// is this item's own reason to exist (`adr/0180` §6: RuStore's undocumented default is four weeks).
/// There is deliberately no <c>notification</c> property on this type at all: adding one that a future
/// caller might populate "just this once" would be the exact regression `adr/0179` §3's whole design
/// depends on never happening, so the shape itself makes it impossible rather than merely discouraged.</para>
/// </summary>
internal sealed record RuStoreMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data,
    [property: JsonPropertyName("android")] RuStoreAndroidConfig Android);

/// <summary>
/// <see cref="Ttl"/>'s wire format is this item's own second, smaller, genuinely unverified guess -
/// named honestly rather than asserted. RuStore's send API "was designed to provide a drop-in
/// replacement for Firebase" (`adr/0180` §1, quoting RuStore's own documentation), and FCM v1's
/// identically-named <c>android.ttl</c> field is a <c>google.protobuf.Duration</c>, whose JSON
/// representation is a decimal number of seconds suffixed with a literal <c>"s"</c> (e.g. <c>"300s"</c>)
/// - this class reproduces that shape on the strength of the drop-in-replacement claim, not from a
/// RuStore-specific example this item could find. <see cref="RuStorePushSenderTests"/> pins down exactly
/// what this class currently sends; whether RuStore's parser actually accepts it is exactly the kind of
/// thing only a real send against a real project proves, and none exists yet
/// (this item's own report is explicit about that).
/// </summary>
internal sealed record RuStoreAndroidConfig(
    [property: JsonPropertyName("ttl")] string Ttl);

/// <summary>
/// RuStore's own documented error body - `code`, `message`, `status`, the HTTP status matching `code`
/// (`adr/0180` §9). <see cref="RuStorePushSender"/> keys revocation-relevant classification on
/// <see cref="Status"/> (falling back to <see cref="Code"/> only where `26-04`'s own backlog item's error
/// table names both), never on <see cref="Message"/> - RuStore's own published example of a
/// malformed-token response carries a Firebase-flavoured message string ("The registration token is not
/// a valid FCM registration token") inside a RuStore body, which is real evidence for trusting the
/// enumerated field over the prose one.
/// </summary>
internal sealed record RuStoreErrorEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string? Status);
