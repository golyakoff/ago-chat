namespace Ago.Chat.Domain;

/// <summary>
/// `24-12`: which table <c>access_records.resource_id</c> points into, when the access named a
/// specific row rather than a whole tenant or the whole deployment - the same "a bare id column needs
/// a discriminator to say what it holds" reasoning <see cref="AccessRecordActorKind"/>'s own remarks
/// give, applied to the resource side of the same row instead of the actor side.
///
/// <para><see langword="null"/> (no member at all) covers the two access kinds with no single
/// resource: <see cref="AccessRecordKind.OwnerSiteList"/> (every tenant, not one) and
/// <see cref="AccessRecordKind.OwnerSiteDetail"/> (the site itself is already named by
/// <c>access_records.site_id</c>, so a second, redundant pointer to the same row would say nothing a
/// reader could not already read off <c>site_id</c>).</para>
/// </summary>
public enum AccessRecordResourceKind
{
    /// <summary><see cref="AccessRecordKind.CrossConversationHistoryRead"/>'s own resource - the
    /// historical conversation that was opened, not the conversation the requesting operator was
    /// actually assigned to.</summary>
    Conversation,

    /// <summary><see cref="AccessRecordKind.OwnerChannelIdentityUnlink"/>'s own resource.</summary>
    ChannelIdentity,

    /// <summary><see cref="AccessRecordKind.OwnerModuleGrant"/>/<see cref="AccessRecordKind.OwnerModuleRevoke"/>'s
    /// own resource.</summary>
    EnabledModule,
}
