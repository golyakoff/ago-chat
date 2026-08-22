namespace Ago.Chat.Application.UseCases.CreateAttachment;

/// <summary>
/// Bound from <c>AttachmentRateLimit:*</c> config keys - the `3-05` shape (`MessageSendRateLimitOptions`)
/// with a third bucket: unlike sending a message, creating an attachment is something both a visitor
/// <em>and</em> an operator do, so each side gets its own budget, on top of the shared per-site one.
///
/// Defaults are a starting point, not measured or load-tested, same caveat as
/// <see cref="AttachmentOptions"/>.
/// </summary>
public sealed class AttachmentRateLimitOptions
{
    public const string SectionName = "AttachmentRateLimit";

    public int PerVisitorCapacity { get; set; } = 5;

    public double PerVisitorRefillPerSecond { get; set; } = 5.0 / 60;

    public int PerOperatorCapacity { get; set; } = 20;

    public double PerOperatorRefillPerSecond { get; set; } = 20.0 / 60;

    public int PerSiteCapacity { get; set; } = 50;

    public double PerSiteRefillPerSecond { get; set; } = 50.0 / 60;
}
