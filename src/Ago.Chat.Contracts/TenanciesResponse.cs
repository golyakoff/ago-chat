namespace Ago.Chat.Contracts;

/// <summary>
/// `13-07`/`adr/0068`: `GET /api/v1/me/tenancies`'s response body - every `Site` the calling
/// identity administers, for the console's own tenancy switcher.
/// </summary>
public sealed record TenanciesResponse(IReadOnlyList<TenancyDto> Tenancies);

/// <summary>One tenancy, on the wire - just enough for a switcher to list and select it.</summary>
public sealed record TenancyDto(Guid SiteId, string SiteName);
