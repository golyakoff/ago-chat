using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes for `24-02`'s own use cases - one small vocabulary, the same
/// one-file-per-feature-area shape <see cref="AcceptanceErrors"/> already establishes.</summary>
public static class PublishedDocumentErrors
{
    public static Error Invalid(string reason) => new("Document.Invalid", reason);

    /// <summary>No version - specific or current - exists under the requested key. The one code both
    /// `24-02` read paths share; a caller cannot distinguish "the key itself is unknown" from "the key
    /// is known but that version was never published" without being handed a list of what does exist,
    /// which the published surface has no reason to offer an anonymous reader.</summary>
    public static Error NotFound(string documentKey) => new("Document.NotFound", $"No document found for key '{documentKey}'.");

    /// <summary><see cref="Ago.Chat.Application.Abstractions.DocumentConcurrencyConflictException"/>
    /// survived every retry <c>PublishDocumentVersionHandler</c> allows itself - two publishes for the
    /// same key raced repeatedly. Genuinely conflict-shaped (`409`), not the caller's mistake to fix
    /// and not a transient dependency failure either - retrying the exact same request is the correct
    /// remedy, the same "retry the request" reasoning <c>ConversationErrors.TransferContended</c>'s own
    /// remarks give for an identical shape.</summary>
    public static Error PublishConflict(string documentKey) =>
        new("Document.PublishConflict", $"Publishing under '{documentKey}' conflicted with a concurrent publish; retry.");

    /// <summary>`24-05`: a purpose string that does not parse to a real
    /// <see cref="Ago.Chat.Domain.VisitorConsentPurpose"/> member - the caller's own mistake to fix,
    /// the same "validate the enum, translate the miss at the Application boundary" split
    /// <see cref="ConversationErrors.WidgetConfigInvalidPosition"/> already draws for its own enum.</summary>
    public static Error InvalidPurpose(string reason) => new("Document.InvalidPurpose", reason);

    /// <summary>`24-05`: a site has <see cref="Ago.Chat.Domain.WidgetConfig.RequireContactConsent"/>
    /// turned on, but nothing has ever been published under this purpose's own consent-document key -
    /// the tenant enabled the flag before writing (or finished writing) their own text. Deployment/
    /// tenant-configuration not ready, not the caller's mistake and not a conflict with anything they
    /// sent - the identical shape <see cref="ConversationErrors.SiteAgreementUnavailable"/> already
    /// gives for the equivalent gap on the tenant-registration path.</summary>
    public static Error ConsentDocumentUnavailable(string documentKey) =>
        new("Document.ConsentDocumentUnavailable", $"No published version exists yet for consent document '{documentKey}'.");
}
