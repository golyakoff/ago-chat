namespace Ago.Chat.Domain;

/// <summary>
/// One choice offered by a structured message: a label a person reads, and a value the producer
/// recognises when it comes back.
///
/// <para><b>First-class, not a field inside the payload - and this is the decision the whole item
/// turns on.</b> Actions inside the payload would be simpler to model and would keep AGO Chat's
/// surface smaller. They would also be unreachable: a renderer for a channel with no UI has to
/// <i>enumerate the choices</i> to print them as a numbered list, and it cannot enumerate anything
/// inside a document whose schema it is forbidden to know. Every channel adapter would then need a
/// parser per payload kind, which is exactly the bespoke-per-channel shape
/// <c>reviews/2026-08-26-platform-boundary.md</c> rules out - and the reason it rules it out is that
/// a booking has to work over SMS, where there is no browser to fall back on.</para>
///
/// <para>So the split is: <b>AGO Chat owns the actions' schema and reads it; AGO Chat owns no schema
/// for the payload and never reads it.</b> Two fields, both strings, neither meaning anything to
/// this product.</para>
///
/// <para><b><see cref="Value"/> is opaque, exactly like a payload.</b> It is whatever the producer
/// needs to recognise the choice - an id, a token, an encoded tuple. AGO Chat compares it to nothing
/// and generates none of it. It comes back the way anything comes back here: as an ordinary message
/// the client sends, carrying the producer's own structured content. There is no second endpoint and
/// no routing, because routing an action to a product would mean knowing which product produced it,
/// which is the same knowledge in a different place.</para>
/// </summary>
/// <param name="Label">What a person sees, and what an SMS renderer prints next to a number. Human
/// text, so no charset restriction beyond a length bound - unlike
/// <see cref="MessageContentKind"/>, this value is never a key, a URL segment or an identifier.</param>
/// <param name="Value">What the producer gets back. Opaque.</param>
public sealed record MessageAction
{
    /// <summary>Short enough to fit a button and a line of a text menu. The narrower channel decides
    /// the bound: a label that needs more than this is not a choice, it is prose, and prose belongs
    /// in <see cref="Message.Body"/>.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>Generous enough for an opaque token or a short encoded tuple, small enough that ten
    /// of them cannot become a second payload smuggled past
    /// <see cref="MessagePayload.MaxLength"/>.</summary>
    public const int MaxValueLength = 256;

    public MessageAction(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException(
                "An action's label cannot be empty - it is the only thing a text-only channel has to print.",
                nameof(label));
        }

        if (label.Length > MaxLabelLength)
        {
            throw new ArgumentException(
                $"An action's label cannot exceed {MaxLabelLength} characters.", nameof(label));
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException(
                "An action's value cannot be empty - it is what its producer recognises when the choice comes back.",
                nameof(value));
        }

        if (value.Length > MaxValueLength)
        {
            throw new ArgumentException(
                $"An action's value cannot exceed {MaxValueLength} characters.", nameof(value));
        }

        Label = label.Trim();
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }
}
