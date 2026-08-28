using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary><paramref name="RoleName"/> is the site-local role name the invitee will hold once
/// redeemed (`"Operator"` or `"Admin"`) - a name, not a `roles.id`, because the caller (an admin
/// filling in a console form, or a curl call per this item's own "no UI" scope) has no reason to know
/// the id `5-08`'s seed transaction happened to generate for either row on this particular site.</summary>
public sealed record CreateOperatorInvite(OperatorId RequestedBy, SiteId SiteId, string RoleName);

/// <summary><see cref="Code"/> is the plaintext value, present in this response only - the same
/// "shown exactly once" shape `RegisterWebhookEndpointHandler`'s own `RegisteredWebhookEndpoint`
/// establishes for a different bearer secret in this codebase.</summary>
public sealed record CreatedOperatorInvite(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt);
