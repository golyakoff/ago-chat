namespace Ago.Chat.Domain;

/// <summary>
/// A named, resource:action permission (adr/0016) - domain vocabulary, not an infrastructure detail,
/// which is why it lives here rather than in <c>Ago.Chat.Application</c> or the RBAC storage that
/// implements the check. Only the permissions Stage 1 actually checks exist so far; more arrive with
/// their first real caller (authorization.md's deferred-permissions list), never speculatively ahead
/// of one.
/// </summary>
public readonly record struct Permission(string Value)
{
    public static readonly Permission ConversationRead = new("conversation:read");
    public static readonly Permission ConversationSend = new("conversation:send");
    public static readonly Permission ConversationAssign = new("conversation:assign");

    public override string ToString() => Value;
}
