namespace Ago.Chat.Domain;

/// <summary>
/// The structured half of a message: a <see cref="MessageContentKind"/>, an optional opaque
/// <see cref="MessagePayload"/>, and up to <see cref="MaxActions"/> <see cref="MessageAction"/>s.
///
/// <para><b>Additive, never a replacement for prose - and that is the rendering contract.</b> A
/// message that carries this still carries a <see cref="Message.Body"/>, and the body is still
/// required. That single rule is what makes structured content work on a channel with no UI, and it
/// is worth stating as a rule rather than leaving as a consequence:</para>
/// <list type="bullet">
///   <item><b><see cref="Message.Body"/> is the fallback, and it is mandatory.</b> Any renderer, on
///   any channel, can always print it. A producer that writes a payload and a body that does not
///   describe it has produced a message that is broken over SMS, and nothing in AGO Chat can detect
///   that - which is why the rule is written down here rather than enforced.</item>
///   <item><b><see cref="Payload"/> is an enrichment a renderer may use instead.</b> A browser draws
///   a card from it; a text channel ignores it entirely and loses nothing but polish.</item>
///   <item><b><see cref="Actions"/> are the choices, and they carry labels precisely so that a text
///   renderer can number them.</b> This is the half that would be unreachable if actions lived
///   inside the payload - see <see cref="MessageAction"/>.</item>
/// </list>
///
/// <para>The worked example of the same content rendered both ways lives in
/// <c>Ago.Chat.Domain.Tests.StructuredContentRenderingTests</c>, which is the item's own proof that
/// the shape survives a channel with no browser.</para>
///
/// <para><b>Everything here is optional except the kind.</b> A payload with no actions is a card
/// nobody has to answer; actions with no payload are a plain "pick one", which is the shape a text
/// channel would produce natively. Requiring a payload would force a producer to invent an empty
/// one, and inventing data to satisfy a validator is how a validator stops meaning anything.</para>
/// </summary>
public sealed record MessageContent
{
    /// <summary>
    /// Ten.
    ///
    /// <para><b>The bound comes from the weakest channel, not from the database.</b> A numbered
    /// choice a person answers by replying with a digit stops being answerable long before ten - so
    /// the limit that binds is the one SMS imposes, and a storage-derived number would have been
    /// larger and less meaningful. Unmeasured, like every other number here, but not arbitrary:
    /// it is the point past which the *rendering contract above* stops being honest.</para>
    /// </summary>
    public const int MaxActions = 10;

    private MessageContent(MessageContentKind kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions)
    {
        Kind = kind;
        Payload = payload;
        Actions = actions;
    }

    public MessageContentKind Kind { get; }

    /// <summary>Opaque to AGO Chat. Absent for a message whose whole content is its choices.</summary>
    public MessagePayload? Payload { get; }

    /// <summary>Empty for a message nobody has to answer.</summary>
    public IReadOnlyList<MessageAction> Actions { get; }

    public static MessageContent Create(
        MessageContentKind kind, MessagePayload? payload = null, IReadOnlyList<MessageAction>? actions = null)
    {
        var resolved = actions ?? [];

        if (resolved.Count > MaxActions)
        {
            throw new ArgumentException(
                $"A message cannot offer more than {MaxActions} actions; got {resolved.Count}. " +
                "The bound is what a person can answer as a numbered choice over a text channel, not a storage limit.",
                nameof(actions));
        }

        if (resolved.Any(action => action is null))
        {
            throw new ArgumentException("An action cannot be null.", nameof(actions));
        }

        // Duplicate values would make a reply ambiguous to the producer that has to interpret it -
        // the one thing about actions AGO Chat can check without knowing what any of them mean.
        var distinctValues = resolved.Select(action => action.Value).Distinct(StringComparer.Ordinal).Count();
        if (distinctValues != resolved.Count)
        {
            throw new ArgumentException(
                "Two actions on one message cannot share a value - whoever produced them could not tell the " +
                "replies apart.",
                nameof(actions));
        }

        return new MessageContent(kind, payload, [.. resolved]);
    }

    /// <summary>
    /// Rebuilds an instance from values that were already validated when they were written -
    /// <see cref="Message.Content"/>'s read path, and nothing else.
    ///
    /// <para>Internal and unvalidated on purpose. Re-running <see cref="Create"/>'s checks on every
    /// read would mean a row that somehow held eleven actions threw while *reading* a conversation's
    /// history rather than while writing it, which turns one bad write into a permanently unreadable
    /// transcript. The checks that matter on the way back in are the ones the value objects do
    /// themselves, in their own converters.</para>
    /// </summary>
    internal static MessageContent Materialize(
        MessageContentKind kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions) =>
        new(kind, payload, actions);
}
