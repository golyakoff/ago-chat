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

    // `6-02`: dedicated, not a reuse of ConversationAssign - adr/0016 chose granular permissions
    // specifically so a future custom role (e.g. a supervisor who may close conversations but not
    // reassign them) can grant one without the other; the marginal cost of one more named permission
    // is small next to that flexibility.
    public static readonly Permission ConversationClose = new("conversation:close");

    // `5-08`: the admin/supervisor role's own two permissions (authorization.md's "Permissions and
    // roles beyond Stage 1" - deferred there specifically until the console needed a real caller).
    // SiteConfigure gates the one caller this item actually builds (the site-wide conversation
    // list - GetAllConversationsForSiteHandler); SiteManageOperators has no caller yet (granting the
    // Operator role itself stays seed-script-only, per this item's own scope note) but is added now,
    // alongside its sibling, because adr/0016's naming convention names both together as the admin
    // role's permission pair - splitting them into two separate items would have meant re-deriving
    // the same naming decision twice for no reason.
    public static readonly Permission SiteConfigure = new("site:configure");
    public static readonly Permission SiteManageOperators = new("site:manage_operators");

    // `5-08`: the moderation action paired with the admin role (authorization.md) - checked by
    // DeleteAttachmentHandler the same way every other permission is checked, granted only to the
    // seeded "Admin" role, never "Operator".
    public static readonly Permission AttachmentDelete = new("attachment:delete");

    public override string ToString() => Value;
}
