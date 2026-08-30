using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// One converter per strongly-typed id (coding-style.md) - explicit and compiled, rather than one
/// reflection-based generic converter, since there are only five of these and reflection would run
/// per row materialized.
/// </summary>
internal static class IdConverters
{
    public static readonly ValueConverter<SiteId, Guid> Site = new(id => id.Value, value => new SiteId(value));
    public static readonly ValueConverter<VisitorId, Guid> Visitor = new(id => id.Value, value => new VisitorId(value));
    public static readonly ValueConverter<OperatorId, Guid> Operator = new(id => id.Value, value => new OperatorId(value));
    public static readonly ValueConverter<ConversationId, Guid> Conversation = new(id => id.Value, value => new ConversationId(value));
    public static readonly ValueConverter<MessageId, Guid> Message = new(id => id.Value, value => new MessageId(value));
    public static readonly ValueConverter<AttachmentId, Guid> Attachment = new(id => id.Value, value => new AttachmentId(value));
    public static readonly ValueConverter<WebhookEndpointId, Guid> WebhookEndpoint = new(id => id.Value, value => new WebhookEndpointId(value));
    public static readonly ValueConverter<WebhookDeliveryId, Guid> WebhookDelivery = new(id => id.Value, value => new WebhookDeliveryId(value));
    public static readonly ValueConverter<ChannelIdentityId, Guid> ChannelIdentity = new(id => id.Value, value => new ChannelIdentityId(value));
    // `14-12`
    public static readonly ValueConverter<PendingChannelLinkRequestId, Guid> PendingChannelLinkRequest = new(
        id => id.Value, value => new PendingChannelLinkRequestId(value));
    public static readonly ValueConverter<ChannelCredentialId, Guid> ChannelCredential = new(id => id.Value, value => new ChannelCredentialId(value));
    public static readonly ValueConverter<OperatorInviteId, Guid> OperatorInvite = new(id => id.Value, value => new OperatorInviteId(value));
    public static readonly ValueConverter<BillingSubscriptionId, Guid> BillingSubscription = new(id => id.Value, value => new BillingSubscriptionId(value));
    public static readonly ValueConverter<BillingWebhookEventId, Guid> BillingWebhookEvent = new(id => id.Value, value => new BillingWebhookEventId(value));
    public static readonly ValueConverter<ConversationNoteId, Guid> ConversationNote = new(id => id.Value, value => new ConversationNoteId(value));
    public static readonly ValueConverter<TagId, Guid> Tag = new(id => id.Value, value => new TagId(value));

    // `20-07`
    public static readonly ValueConverter<EnabledModuleId, Guid> EnabledModule = new(id => id.Value, value => new EnabledModuleId(value));
    public static readonly ValueConverter<ModuleTaskId, Guid> ModuleTask = new(id => id.Value, value => new ModuleTaskId(value));

    /// <summary>`20-07`: <see cref="ModuleKey"/> is a plain string wrapper (like <see cref="RetentionClass"/>),
    /// not a strongly-typed id over a <see cref="Guid"/> - listed here anyway, alongside every other
    /// value-object converter this file owns, rather than inline per configuration, since two entities
    /// (<see cref="Domain.EnabledModule"/>, <see cref="Domain.ModuleTask"/>) both need it.</summary>
    public static readonly ValueConverter<ModuleKey, string> ModuleKey = new(key => key.Value, value => new ModuleKey(value));

    public static readonly ValueConverter<OperatorId?, Guid?> NullableOperator = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new OperatorId(value.Value) : (OperatorId?)null);

    public static readonly ValueConverter<AttachmentId?, Guid?> NullableAttachment = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new AttachmentId(value.Value) : (AttachmentId?)null);

    public static readonly ValueConverter<MessageId?, Guid?> NullableMessage = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new MessageId(value.Value) : (MessageId?)null);

    /// <summary>`18-01`: <see cref="Message.SiteId"/> - nullable for the same reason
    /// <see cref="NullableAttachment"/>/<see cref="NullableMessage"/> are, a column that does not have
    /// a value for every historical row.</summary>
    public static readonly ValueConverter<SiteId?, Guid?> NullableSite = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new SiteId(value.Value) : (SiteId?)null);
}
