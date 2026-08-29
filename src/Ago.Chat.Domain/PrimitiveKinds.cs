namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`/`adr/0065` decision 4: the closed vocabulary of content kinds a module's step may take -
/// derived from `20-06`'s real booking flow, and nothing it does not need. Each one is an ordinary
/// <see cref="MessageContentKind"/> value; this class exists only so every caller spells the same four
/// strings the same way, instead of each inventing its own literal.
///
/// <para><b>This is AGO Chat's own vocabulary, not a foreign domain's.</b> "A choice list", "a form",
/// "a confirmation card", "a date-and-time picker" describe the shape of an answer a person can give,
/// not what the question is about - the same distinction <see cref="MessageContentKind"/>'s own remarks
/// draw between validating shape and validating meaning. Chat owning these four words is exactly what
/// `adr/0065` decision 4 says: "modules fill them in; modules do not define kinds of their own."</para>
/// </summary>
public static class PrimitiveKinds
{
    /// <summary>A prompt plus a bounded set of <see cref="MessageAction"/>s the visitor picks one of.
    /// The plainest of the four - what a text channel would have produced natively even without this
    /// contract.</summary>
    public const string ChoiceList = "choice_list";

    /// <summary>A single free-text field: a prompt and a label. The one primitive whose answer is not
    /// resolved against <see cref="MessageAction"/> values - see <see cref="ChoiceReplyTextResolver"/>'s
    /// own remarks on why.</summary>
    public const string Form = "form";

    /// <summary>A titled summary the visitor accepts or rejects - payload lines to read, actions to
    /// answer with (typically "Confirm"/"Cancel").</summary>
    public const string ConfirmationCard = "confirmation_card";

    /// <summary>A choice list whose payload additionally carries each option's own start time, for a
    /// richer widget rendering - the actions alone remain sufficient to answer on a text channel
    /// (backlog item's own "actions alone is sufficient... payload.slots is enrichment").</summary>
    public const string DateTimePicker = "date_time_picker";

    /// <summary>Every kind this vocabulary defines, in no particular order - used by validation and by
    /// the architecture guard's own fixtures, never by a runtime branch that would decide behaviour
    /// per kind (that would defeat the very opacity this type exists to preserve outside this file).</summary>
    public static readonly IReadOnlyList<string> All = [ChoiceList, Form, ConfirmationCard, DateTimePicker];

    /// <summary>
    /// The three kinds a visitor answers by picking one of a bounded set of <see cref="MessageAction"/>s
    /// - what <see cref="ChoiceReplyTextResolver"/> and <see cref="PrimitiveTextRenderer"/> both need to
    /// know, since <see cref="Form"/> is answered with raw text instead. Kept here, rather than
    /// hard-coded into each of those two, so the "which kinds are choice-shaped" fact is stated in
    /// exactly one place - the wire contract's own "reply-by-id is the same shape for every kind" rule
    /// depends on this set being exhaustive over three of the four, not guessed at per call site.
    /// </summary>
    public static readonly IReadOnlyList<string> ChoiceShaped = [ChoiceList, ConfirmationCard, DateTimePicker];

    public static bool IsChoiceShaped(string kind) => ChoiceShaped.Contains(kind, StringComparer.Ordinal);
}
