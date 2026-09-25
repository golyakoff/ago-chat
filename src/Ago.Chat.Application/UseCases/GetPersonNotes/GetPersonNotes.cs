using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetPersonNotes;

public sealed record GetPersonNotes(VisitorId PersonId, SiteId SiteId, OperatorId RequestedBy);
