using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.AddPersonNote;

public sealed record AddPersonNote(VisitorId PersonId, SiteId SiteId, OperatorId RequestedBy, string Body);
