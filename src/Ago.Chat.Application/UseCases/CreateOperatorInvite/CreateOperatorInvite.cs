using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary><paramref name="RoleName"/> is the site-local role name the invitee will hold once
/// redeemed (`"Operator"` or `"Admin"`) - a name, not a `roles.id`, because the caller (an admin
/// filling in a console form, or a curl call per this item's own "no UI" scope) has no reason to know
/// the id `5-08`'s seed transaction happened to generate for either row on this particular site.
///
/// <para>`25-73`: <paramref name="Email"/> is now required - refused by
/// <see cref="CreateOperatorInviteHandler"/>, not merely hidden by the console's own form, since
/// Keycloak's own admin-created-user + `execute-actions-email` primitive is what actually delivers
/// this invite now, and that primitive needs an address to create a user for.</para></summary>
public sealed record CreateOperatorInvite(OperatorId RequestedBy, SiteId SiteId, string RoleName, string Email);

/// <summary><see cref="Code"/> is the plaintext value, present in this response only - the same
/// "shown exactly once" shape `RegisterWebhookEndpointHandler`'s own `RegisteredWebhookEndpoint`
/// establishes for a different bearer secret in this codebase.
///
/// <para>`25-73`: <see cref="SendFailed"/> - <see langword="true"/> when Keycloak's own realm relay
/// failed to deliver this invite's email at the SMTP layer (never thrown for that case - this item's
/// own point 6, "not swallowed"). The invite still exists either way; the console's own invite-list
/// screen is where the failure's own SMTP error code is shown, not this creation response, so an admin
/// who is not looking at that screen right now still gets an honest "sent" flag on the response their
/// own click produced.</para></summary>
public sealed record CreatedOperatorInvite(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt, bool SendFailed);
