using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListWebhookEndpoints;

public sealed record ListWebhookEndpoints(OperatorId RequestedBy, SiteId SiteId);
