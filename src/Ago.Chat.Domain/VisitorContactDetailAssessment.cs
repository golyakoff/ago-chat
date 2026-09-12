namespace Ago.Chat.Domain;

/// <summary>
/// `25-58`: an operator's own judgment about a <see cref="VisitorContactDetail"/> that carries a
/// <see cref="VisitorContactDetailKind.Phone"/> or <see cref="VisitorContactDetailKind.Email"/> - "I
/// tried this and it worked" or "I tried this and it did not," never a claim about who controls the
/// address. The same "operator's own report, small closed vocabulary, `Unset` default" shape
/// <see cref="ConversationOutcome"/> already establishes for a different fact an operator asserts about
/// a conversation - <see cref="Unset"/> here is the identical "nobody has said anything yet" default,
/// never itself a settable target (<see cref="VisitorContactDetail.SetAssessment"/> refuses it the same
/// way <see cref="Conversation.SetOutcome"/> refuses <see cref="ConversationOutcome.Unset"/>).
///
/// <para><b>Deliberately not named, or shaped, anything close to <see cref="VisitorContactDetail.Verified"/>
/// or <see cref="ChannelIdentity"/>'s own verified-channel mechanism - this is the distinction the
/// backlog item (`25-58`) itself insists on holding.</b> <see cref="VisitorContactDetail.Verified"/> is
/// reserved for a future <em>evidence-based</em> mechanism - a code the visitor typed back, a real
/// inbound message - and today has no writer that ever sets it (that property's own remarks). A
/// <see cref="ChannelIdentity"/>'s own verification is the same evidentiary kind, applied to a
/// routable address. This type is neither: it is one operator's unaudited, overridable opinion, exactly
/// as fallible as the phone number or address it comments on. Naming this <c>Verified</c>,
/// <c>Confirmed</c> alone, or anything that reads like proof of address ownership would let a future
/// reader mistake an operator's guess for the evidence <see cref="VisitorContactDetail.Verified"/> is
/// reserved for - the reason this lives in its own enum, on its own column, under its own name.</para>
///
/// <para>Stored as the CLR member name via EF's default string conversion - the same reasoning
/// <see cref="VisitorContactDetailKind"/>/<see cref="VisitorContactDetailSource"/> already give for
/// themselves: an ordinal makes reordering this enum a silent data corruption.</para>
/// </summary>
public enum VisitorContactDetailAssessment
{
    /// <summary>The default for every <see cref="VisitorContactDetailKind.Phone"/>/<see cref="VisitorContactDetailKind.Email"/>
    /// row, past and future, until an operator explicitly picks one of the two real values below - and
    /// the permanent, only value for <see cref="VisitorContactDetailKind.Name"/>, which
    /// <see cref="VisitorContactDetail.SetAssessment"/> refuses to move off of at all
    /// (<see cref="VisitorContactDetail"/>'s own remarks on why a name has no
    /// channel to confirm or invalidate the way a phone number or an email address does).</summary>
    Unset,

    /// <summary>An operator asserts this value is good - reached the visitor, or the visitor confirmed
    /// it back, in this operator's own judgment.</summary>
    Confirmed,

    /// <summary>An operator asserts this value is no good - a dead line, a bounced address - without
    /// removing the row itself. Strictly more informative than deleting it outright (the backlog
    /// item's own reasoning): a future reader sees that this value was tried and found wanting, rather
    /// than seeing nothing at all.</summary>
    Invalid,
}
