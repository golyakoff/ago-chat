using System.Text.Json;

namespace Ago.Chat.Domain;

/// <summary>
/// A structured message's content, opaque to AGO Chat: stored, sequenced, delivered and rendered,
/// never branched on.
///
/// <para><b>What is checked, and why exactly this much.</b> Two things, both about the value being a
/// usable instance of its declared type rather than about what it means:</para>
/// <list type="number">
///   <item><b>It parses, and parses as a JSON object.</b> A message is immutable and fans out to
///   every participant plus every history read, forever - so a malformed payload AGO Chat accepted
///   cannot be repaired by its producer and breaks rendering for every reader permanently. Failing
///   at send time returns an error to one caller who can fix it; failing at render time is a defect
///   with no owner. Requiring an <i>object</i> rather than any JSON value is what lets a generic
///   renderer walk named fields without knowing a single field name - a bare array or scalar has no
///   names to walk. Neither check looks at a key.</item>
///   <item><b>It is bounded.</b> See <see cref="MaxLength"/>.</item>
/// </list>
///
/// <para><b>What is deliberately not checked:</b> any key, any value, any schema. AGO Chat owns no
/// schema for this field and must not acquire one - a validator here would be the boundary crossing
/// the whole design exists to prevent, wearing a validator's clothes.</para>
///
/// <para><b>Stored verbatim, not re-serialised.</b> The producer's own bytes are what reach the
/// column and what come back out. Round-tripping through a parsed representation would reorder keys
/// and collapse duplicates, which is invisible until somebody signs or hashes a payload - and a
/// product that does so is entitled to, because AGO Chat promised to carry this and not to read
/// it.</para>
/// </summary>
public readonly record struct MessagePayload
{
    /// <summary>
    /// 16 KB. A ceiling, not a target, and unmeasured - CLAUDE.md's rule against inventing numbers
    /// means saying so rather than implying otherwise.
    ///
    /// <para><b>Why a ceiling has to exist at all:</b> this field rides the message-send path, which
    /// accepts input from unauthenticated visitors on the public internet. An unbounded opaque field
    /// there is an amplification vector, not a validation nicety - one send is stored forever, fanned
    /// out to every connected participant, and replayed on every history read.</para>
    ///
    /// <para><b>Why this size:</b> twice <see cref="MessageBody.MaxLength"/>, because a structured
    /// document legitimately spends bytes on repeated field names and nesting that prose does not,
    /// and because a page of history is up to <c>pageSize</c> of these at once - the response, not
    /// the row, is what makes a generous limit expensive.</para>
    /// </summary>
    public const int MaxLength = 16_384;

    public MessagePayload(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A message payload cannot be empty.", nameof(value));
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A message payload cannot exceed {MaxLength} characters.", nameof(value));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"A message payload must be well-formed JSON: {exception.Message}", nameof(value), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "A message payload must be a JSON object - a renderer that knows none of its field names " +
                    "still has to be able to walk it by name.",
                    nameof(value));
            }
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
