namespace Ago.Chat.Application.UseCases.GenerateReplyDraft;

/// <summary>
/// Bound from `ReplyDraftRateLimit:*` config keys - `AttachmentRateLimitOptions`'s own two-bucket
/// shape (a caller's own budget, then a shared site-wide one), minus the visitor bucket that class
/// also has: this feature has no visitor entry point at all (`GenerateReplyDraftAsOperator`'s own
/// remarks), so there is no third caller kind to give a budget to.
///
/// <para><b>Why a real cap, not a config value nobody enforces.</b> `19-01`'s own Done-when: "a
/// rate/cost cap exists and is enforced... not just that a config value exists" - every call to the
/// real provider costs real money (`resilience.md`), unlike every other console interaction this
/// project has built so far, which is `CLAUDE.md`'s "performance claims need numbers" rule applied to
/// a cost claim instead of a latency one. <see cref="GenerateReplyDraftHandler"/> checks the
/// per-operator bucket before the per-site one - the same ordering `CreateAttachmentHandler` uses and
/// for the identical reason: a caller who was never going to pass their own bucket should not also
/// spend a share of the shared site budget finding that out.</para>
///
/// <para>Defaults are a reasoned starting point, not measured or load-tested - the same caveat every
/// other rate-limit options class in this codebase carries. Ten drafts an hour per operator is enough
/// for genuine per-conversation use without turning the button into a way to generate free-form LLM
/// text at scale; thirty an hour per site bounds one very active site's total spend regardless of how
/// many operators it has.</para>
/// </summary>
public sealed class ReplyDraftRateLimitOptions
{
    public const string SectionName = "ReplyDraftRateLimit";

    public int PerOperatorCapacity { get; set; } = 10;

    public double PerOperatorRefillPerSecond { get; set; } = 10.0 / 3600;

    public int PerSiteCapacity { get; set; } = 30;

    public double PerSiteRefillPerSecond { get; set; } = 30.0 / 3600;
}
