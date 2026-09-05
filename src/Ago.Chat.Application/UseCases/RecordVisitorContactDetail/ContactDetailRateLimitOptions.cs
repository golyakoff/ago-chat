namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `23-09`: rate limiting for <c>RecordVisitorContactDetailHandler.HandleAsVisitorAsync</c>, the first
/// unauthenticated-by-permission write this handler gains - the operator path is already gated by
/// <c>Permission.ConversationSend</c>, which no rate limit needs to stand in for. Two buckets, not
/// three: unlike `14-15`'s phone verification, this call sends nothing to the value it records (no
/// SMS, no call - `decisions.md` §4's "no verification for a callback"; a human calls back later), so
/// there is no per-phone-number target to protect the way <c>PhoneVerificationRateLimitOptions</c>
/// protects one. What is left is the identical shape <c>CreateAttachmentHandler</c>'s own visitor path
/// already checks: a per-visitor bucket, then a per-site bucket, cheapest-reject-first.
///
/// <para>Defaults are a starting point, not measured or load-tested - the same caveat every
/// `*RateLimitOptions` class in this codebase carries.</para>
/// </summary>
public sealed class ContactDetailRateLimitOptions
{
    public const string SectionName = "ContactDetailRateLimit";

    public int PerVisitorCapacity { get; set; } = 5;

    public double PerVisitorRefillPerSecond { get; set; } = 5.0 / 3600;

    public int PerSiteCapacity { get; set; } = 100;

    public double PerSiteRefillPerSecond { get; set; } = 100.0 / 3600;
}
