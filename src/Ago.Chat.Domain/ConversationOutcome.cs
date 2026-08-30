namespace Ago.Chat.Domain;

/// <summary>
/// `18-10`: what a conversation actually led to, in the operator's own words - a closed, small
/// vocabulary, matching <see cref="MessageAuthorKind"/>'s own "closed vocabulary, not free text"
/// precedent (that type's own remarks). A free-text outcome field would be unreportable (no query can
/// aggregate prose) and would need its own moderation/parsing story this item does not need to invent -
/// the same reasoning that keeps <see cref="Tag"/> a picked-from-a-vocabulary label rather than a
/// free-text field on a conversation.
///
/// <para><b>This is an operator's own report, not a verified sale record.</b> AGO Chat has no concept
/// of an order or a payment anywhere in its data model, and integrating with a shop's own order system
/// to get one honestly is out of this item's scope (`ago-business/decisions/0009`'s Level 4 rejection -
/// CRM/e-commerce depth is a different product). Every caller that reads this value, or a report built
/// from it, must say so: a conversion rate computed from this enum is real and useful, and it is not
/// the same claim as "N% of chats resulted in a verified sale."</para>
/// </summary>
public enum ConversationOutcome
{
    /// <summary>The default for every conversation, past and future, until an operator explicitly
    /// records one of the three real values below. Never itself a settable target -
    /// <see cref="Conversation.SetOutcome"/> refuses it - so there is no path back to "no outcome
    /// recorded" once an operator has picked one; the closest a mind-changing operator gets is picking
    /// a different real value instead.</summary>
    Unset,

    Converted,

    NotConverted,

    /// <summary>Neither a sale nor a clean "no" - the visitor needs something the operator could not
    /// resolve in this conversation (a quote, a manager's approval, a delivery-date check). Counted in
    /// neither half of a conversion rate's numerator or denominator (`IConversionReportReadStore`'s own
    /// remarks) - it is not yet known whether this will convert, and treating it as either "yes" or
    /// "no" would be a guess this item has no basis for making.</summary>
    FollowUpNeeded,
}
