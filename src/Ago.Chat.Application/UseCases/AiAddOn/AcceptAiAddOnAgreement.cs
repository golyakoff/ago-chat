using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: the tenant accepts the AI add-on agreement. <paramref name="Version"/> is the version the
/// caller says they were shown - checked against the currently published one rather than trusted, so a
/// console that had the page open across a republish cannot record an acceptance of text nobody is
/// reading any more.
/// </summary>
public sealed record AcceptAiAddOnAgreement(
    SiteId SiteId,
    OperatorId AcceptedBy,
    string Version,
    string? ClientIp = null,
    string? UserAgent = null);
