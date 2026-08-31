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

    /// <summary>
    /// `19-03`: a prompt, no actions, and the task closes - the fifth member this vocabulary has ever
    /// needed, added by the second module to exist rather than guessed at by the first. `adr/0065`
    /// decision 7 already names the principle ("an escape to a human always exists and cannot be
    /// suppressed by the module") for the *unreachable* case, which the routing handler enforces itself
    /// without any help from the module; this is the *reachable-but-unsure* case, where only the module
    /// knows it has run out of confidence, and the module has to be able to say so. Handled structurally
    /// by <see cref="RouteConversationToModule.RouteConversationToModuleHandler"/>: it recognises this
    /// one kind by value, exactly the way it already recognises <see cref="IsChoiceShaped"/> membership,
    /// and forces the task closed regardless of the module's own <c>complete</c> flag - the "cannot be
    /// suppressed" half of decision 7, now given a second, module-triggered path to the same outcome.
    /// This is knowledge of *this vocabulary's own fifth word*, not of any module's domain - see
    /// <c>adr/0081</c> for the full reasoning and the alternatives rejected.</summary>
    public const string Escalate = "escalate";

    /// <summary>
    /// `20-09`: a <see cref="Form"/> whose one field must be a phone number this system has already
    /// proven the visitor can be reached on (`14-15`), not merely typed - the sixth member this
    /// vocabulary has ever needed, added the identical way <see cref="Escalate"/> was
    /// (<c>adr/0081</c>'s template: a module signals a structural fact by *which* primitive it sends,
    /// never by Chat inspecting the payload).
    ///
    /// <para>Wire-shaped identically to <see cref="Form"/> (a prompt, a field id, a field label) -
    /// nothing about the payload differs, which is exactly why this is a distinct <em>kind</em> rather
    /// than a flag on <see cref="Form"/>: `adr/0065` decision 4's "modules fill them in; modules do not
    /// define kinds of their own" means the fact "this reply must carry proof, not just a value" has to
    /// be expressed in the one field a module can use to say anything to Chat at all - <c>kind</c>.
    /// Handled structurally by
    /// <see cref="RouteConversationToModule.RouteConversationToModuleHandler"/>: a reply against a step
    /// of this kind is checked against an active, verified <c>ChannelIdentity</c> for the visitor
    /// before it is ever forwarded to the module - see that handler's own remarks and
    /// <c>docs/adr/0082-*</c> for the full reasoning.</para>
    /// </summary>
    public const string VerifiedPhoneForm = "verified_phone_form";

    /// <summary>Every kind this vocabulary defines, in no particular order - used by validation and by
    /// the architecture guard's own fixtures, never by a runtime branch that would decide behaviour
    /// per kind (that would defeat the very opacity this type exists to preserve outside this file).</summary>
    public static readonly IReadOnlyList<string> All =
        [ChoiceList, Form, ConfirmationCard, DateTimePicker, Escalate, VerifiedPhoneForm];

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
