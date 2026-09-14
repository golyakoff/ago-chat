using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>`25-04`'s own error vocabulary - the same one-file-per-feature-area shape
/// <see cref="AcceptanceErrors"/> and <c>ConversationErrors</c> establish.
///
/// <para><b>Three distinct refusals for the three distinct missing facts</b>, not one
/// "AiAddOn.NotReady". A tenant who has accepted but not declared is in a different place from one who
/// has declared but not accepted, and the console can only tell them which control to press if the API
/// tells them which fact is missing - the operator-facing half of the same distinction
/// <c>Ago.Chat.Domain.AiProcessingBasisDeclaration</c>'s own remarks draw in the schema.</para>
/// </summary>
public static class AiAddOnErrors
{
    public static Error Forbidden(string reason) => new("AiAddOn.Forbidden", reason);

    /// <summary>The site has no effective quantity of the add-on module - nothing to enable.</summary>
    public static Error NotPurchased(string reason) => new("AiAddOn.NotPurchased", reason);

    /// <summary>No published agreement exists in this deployment yet, so nothing can be accepted and
    /// nothing can be enabled. A deployment fault, surfaced rather than swallowed.</summary>
    public static Error AgreementNotPublished(string reason) => new("AiAddOn.AgreementNotPublished", reason);

    /// <summary>The current version of the agreement has not been accepted by this tenant - either never,
    /// or only an older version (the acceptance names the version, `24-01`).</summary>
    public static Error AgreementNotAccepted(string reason) => new("AiAddOn.AgreementNotAccepted", reason);

    /// <summary>The tenant has not declared they hold a lawful basis for their visitors' data reaching
    /// the vendor - decision 5's own distinct fact, and its own distinct refusal.</summary>
    public static Error BasisNotDeclared(string reason) => new("AiAddOn.BasisNotDeclared", reason);

    /// <summary>The version the caller says they read is not the one currently published - a tenant
    /// accepting a version they were shown before a republish must be shown the new text instead.</summary>
    public static Error AgreementVersionStale(string reason) => new("AiAddOn.AgreementVersionStale", reason);
}
