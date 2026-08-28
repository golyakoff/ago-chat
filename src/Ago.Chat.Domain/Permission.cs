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

    // `6-03`: flagged ahead of time by authorization.md's own "Permissions and roles beyond Stage 1"
    // list ("site:manage_webhooks - not designed yet, but Stage 6... will need exactly this kind of
    // check") - named `webhook:manage` instead, matching this project's `resource:action` convention
    // (`site:manage_webhooks` reads as an action on `site`, but the resource being managed is the
    // webhook endpoint itself, the same reasoning `AttachmentDelete` uses over a hypothetical
    // `conversation:delete_attachment`). Gates registration, listing, revocation, and delivery-history
    // reads alike - this item's own scope: "reading delivery history is not more sensitive than
    // managing the endpoint that produces it."
    public static readonly Permission WebhookManage = new("webhook:manage");

    // `14-02`: the same `resource:action` reasoning as `WebhookManage` right above - the resource being
    // managed is the channel credential (`ChannelCredential`), not the site itself. Gates registering
    // and revoking a channel's bot token; there is no separate read permission because, per `adr/0069`,
    // there is nothing to read back - the console only ever learns whether a credential is active, never
    // its value, so "manage" already covers the one query this item ships.
    public static readonly Permission ChannelManage = new("channel:manage");

    // `16-02`: two dedicated permissions, not a reuse of SiteConfigure - the same granular-permission
    // shape adr/0016 already draws everywhere else (ConversationClose is separate from
    // ConversationAssign; AttachmentDelete is separate from ordinary conversation actions).
    // SiteConfigure gates reversible administrative changes (branding, widget settings); bundling
    // irreversible whole-account destruction into it would let anyone who can tweak a widget's colour
    // also permanently destroy the tenant - a materially larger blast radius than the permission's name
    // implies. Both are Admin-role-only (RegisterSiteHandler.AdminRolePermissions,
    // MintDemoTenantHandler.AdminRolePermissions, and `ago-deploy/seed/create-demo-tenant.sh`'s own
    // restatement - never the base Operator role.
    public static readonly Permission SiteErase = new("site:erase");

    // Conversation-scoped, narrower than SiteErase - a tenant deleting one visitor's conversation on
    // request does not need, and must not require, the power to destroy the whole account.
    public static readonly Permission ConversationErase = new("conversation:erase");

    // `16-03`: a dedicated permission, not a reuse of SiteConfigure or SiteErase - the same
    // granular-permission reasoning SiteErase's own remarks give, applied to a third, distinct blast
    // radius. Export is not reversible-config-change shaped (SiteConfigure) and it is not
    // destruction-shaped (SiteErase) - it is "hand a complete copy of everything this tenant holds to
    // whoever asks", which deserves its own named capability so a future custom role can grant "may
    // configure the widget" without also granting "may walk out with every visitor's conversation
    // history." Admin-role-only, the same three restatements as SiteErase/ConversationErase
    // (RegisterSiteHandler.AdminRolePermissions, MintDemoTenantHandler.AdminRolePermissions, and
    // `ago-deploy/seed/create-demo-tenant.sh`'s own restatement) - see this item's own commit-prep
    // notes for why the seed script's restatement was not reached by this change.
    public static readonly Permission SiteExport = new("site:export");

    public override string ToString() => Value;
}
