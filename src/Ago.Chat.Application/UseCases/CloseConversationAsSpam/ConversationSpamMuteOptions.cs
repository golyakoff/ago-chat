namespace Ago.Chat.Application.UseCases.CloseConversationAsSpam;

/// <summary>
/// `23-69`: how long a "close as spam" auto-mute lasts - bound from <c>ConversationSpamMute:*</c>
/// config keys, the same options-class shape <c>ConversationCreateRateLimitOptions</c> already
/// establishes for a sibling tunable this same handler family reads.
///
/// <para><b><c>DefaultDuration</c> is a configurable default, not a measured number - CLAUDE.md's own
/// "do not invent numbers... measure or stay silent" rule, honoured by naming this what it is.</b> The
/// backlog item's own Question 1 ("Decided: it auto-mutes the visitor for a window of time") names the
/// shape but deliberately not a duration. <b>24 hours</b> is this worker's own engineering judgement,
/// reasoned as follows and nothing more: long enough that a genuine one-off flood (the case this item's
/// own "Why abuse is not the argument against it" section is written for) cannot simply be retried a
/// few minutes later by the same visitor identity, short enough that a misjudged, ordinary customer
/// caught in it is not shut out for anything close to permanent - the same order of magnitude
/// <c>OperatorInviteRateLimited</c>'s own literal `24`-hour window already uses elsewhere in this
/// codebase for an unrelated but structurally similar "bounded cooldown, not a wall" tradeoff. It is
/// explicitly <b>not</b> load-tested or derived from any observed abuse pattern - there is none to
/// observe yet, this product has no live spam traffic - and the tenant's own reversal path
/// (<c>LiftVisitorRestrictionHandler</c>, gated on <c>Permission.ConversationMarkSpam</c>) exists
/// precisely because this number is a guess a real operator can override the moment it is visibly
/// wrong, per this item's own "an irreversible judgement made in one click by a tired person is a
/// worse tool than no tool."</para>
/// </summary>
public sealed class ConversationSpamMuteOptions
{
    public const string SectionName = "ConversationSpamMute";

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromHours(24);
}
