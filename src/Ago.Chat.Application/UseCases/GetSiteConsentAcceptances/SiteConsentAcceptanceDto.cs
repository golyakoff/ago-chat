namespace Ago.Chat.Application.UseCases.GetSiteConsentAcceptances;

/// <summary>
/// `23-37`. Deliberately narrower than <see cref="Domain.AcceptanceRecord"/> itself - no
/// <c>ClientIp</c>/<c>UserAgent</c>. Those two fields exist on the record for the same reason
/// <see cref="Domain.AcceptanceRecord"/>'s own remarks give ("enough to be credible, not a surveillance
/// log"), but that reasoning was about what the *record* should hold to be defensible, not about what
/// an ordinary tenant-facing screen should *display* every time somebody asks "who accepted, and when".
/// This item's own Done-when asks only "which version did this person accept, and when" - answerable
/// from <see cref="DocumentVersion"/>/<see cref="AcceptedAt"/> alone - and `personal-data.md`'s own
/// register already treats an IP address as more directly identifying than a bare subject id (the
/// `visitors` row holds no PII at all; an IP address is the kind of fact that narrows to a device or a
/// location). Decided explicitly rather than left implicit - `docs/adr/0146-*` - because a tenant who
/// genuinely needs the record's full evidentiary shape has no way to get it from this screen today;
/// the columns are not deleted, only not read back by this one screen.</summary>
public sealed record SiteConsentAcceptanceDto(string SubjectKind, Guid SubjectId, string DocumentVersion, DateTimeOffset AcceptedAt);
