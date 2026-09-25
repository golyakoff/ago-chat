using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetPersons;

/// <summary>`adr/0184` decision 4: the console's display-merge read - "who are these people", by the
/// opaque person ids a booking or a contact row carries. One or many ids in one call: a bookings screen
/// draws a page of rows and needs every name on it at once, not one request per row.</summary>
public sealed record GetPersons(SiteId SiteId, OperatorId RequestedBy, IReadOnlyList<VisitorId> PersonIds);
