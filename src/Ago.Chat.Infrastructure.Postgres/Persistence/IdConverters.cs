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
    // `23-32`
    public static readonly ValueConverter<TeamMessageId, Guid> TeamMessage = new(id => id.Value, value => new TeamMessageId(value));
    public static readonly ValueConverter<AttachmentId, Guid> Attachment = new(id => id.Value, value => new AttachmentId(value));
    public static readonly ValueConverter<WebhookEndpointId, Guid> WebhookEndpoint = new(id => id.Value, value => new WebhookEndpointId(value));
    public static readonly ValueConverter<WebhookDeliveryId, Guid> WebhookDelivery = new(id => id.Value, value => new WebhookDeliveryId(value));
    // `23-19`
    public static readonly ValueConverter<ChannelDeliveryId, Guid> ChannelDelivery = new(id => id.Value, value => new ChannelDeliveryId(value));
    public static readonly ValueConverter<ChannelIdentityId, Guid> ChannelIdentity = new(id => id.Value, value => new ChannelIdentityId(value));
    // `14-12`
    public static readonly ValueConverter<PendingChannelLinkRequestId, Guid> PendingChannelLinkRequest = new(
        id => id.Value, value => new PendingChannelLinkRequestId(value));
    public static readonly ValueConverter<ChannelCredentialId, Guid> ChannelCredential = new(id => id.Value, value => new ChannelCredentialId(value));
    public static readonly ValueConverter<OperatorInviteId, Guid> OperatorInvite = new(id => id.Value, value => new OperatorInviteId(value));
    public static readonly ValueConverter<BillingSubscriptionId, Guid> BillingSubscription = new(id => id.Value, value => new BillingSubscriptionId(value));
    public static readonly ValueConverter<BillingWebhookEventId, Guid> BillingWebhookEvent = new(id => id.Value, value => new BillingWebhookEventId(value));
    public static readonly ValueConverter<ConversationNoteId, Guid> ConversationNote = new(id => id.Value, value => new ConversationNoteId(value));

    // `24-01`
    public static readonly ValueConverter<AcceptanceRecordId, Guid> AcceptanceRecord = new(id => id.Value, value => new AcceptanceRecordId(value));
    // `24-02`
    public static readonly ValueConverter<DocumentId, Guid> Document = new(id => id.Value, value => new DocumentId(value));
    public static readonly ValueConverter<PublishedDocumentVersionId, Guid> PublishedDocumentVersion = new(
        id => id.Value, value => new PublishedDocumentVersionId(value));
    // `23-03`
    public static readonly ValueConverter<ConversationAssignmentId, Guid> ConversationAssignment = new(
        id => id.Value, value => new ConversationAssignmentId(value));
    public static readonly ValueConverter<TagId, Guid> Tag = new(id => id.Value, value => new TagId(value));
    // `14-14`
    public static readonly ValueConverter<VisitorContactDetailId, Guid> VisitorContactDetail = new(
        id => id.Value, value => new VisitorContactDetailId(value));

    // `14-15`
    public static readonly ValueConverter<PendingPhoneVerificationId, Guid> PendingPhoneVerification = new(
        id => id.Value, value => new PendingPhoneVerificationId(value));

    // `20-07`
    public static readonly ValueConverter<EnabledModuleId, Guid> EnabledModule = new(id => id.Value, value => new EnabledModuleId(value));
    public static readonly ValueConverter<ModuleTaskId, Guid> ModuleTask = new(id => id.Value, value => new ModuleTaskId(value));

    // `20-11`
    public static readonly ValueConverter<ModuleTaskChannelPreferenceId, Guid> ModuleTaskChannelPreference = new(
        id => id.Value, value => new ModuleTaskChannelPreferenceId(value));

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

    /// <summary>`14-13`: <see cref="Visitor.PreferredChannelIdentityId"/> - null until an operator sets
    /// one, the same "no value for every row" shape <see cref="NullableOperator"/> already establishes.</summary>
    public static readonly ValueConverter<ChannelIdentityId?, Guid?> NullableChannelIdentity = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new ChannelIdentityId(value.Value) : (ChannelIdentityId?)null);

    /// <summary>`23-86`: <see cref="BillingSubscription.OptionKey"/> - null for the account's own base
    /// row, a real value for every option row, the same "no value for every row" shape
    /// <see cref="NullableOperator"/> already establishes for a strongly-typed id, applied here to a
    /// plain string wrapper (<see cref="ModuleKey"/>'s own converter is this one's non-nullable
    /// sibling).</summary>
    public static readonly ValueConverter<BillingOptionKey?, string?> NullableBillingOptionKey = new(
        key => key.HasValue ? key.Value.Value : null,
        value => value != null ? new BillingOptionKey(value) : (BillingOptionKey?)null);

    /// <summary>`25-43`: <see cref="PricedResource"/>'s own strongly-typed id - the identical
    /// <see cref="Document"/> converter above, restated for the new aggregate.</summary>
    public static readonly ValueConverter<PricedResourceId, Guid> PricedResource = new(
        id => id.Value, value => new PricedResourceId(value));

    /// <summary>`25-43`: <see cref="PublishedPriceVersion"/>'s own strongly-typed id - the identical
    /// <see cref="PublishedDocumentVersion"/> converter above, restated for the new child row.</summary>
    public static readonly ValueConverter<PublishedPriceVersionId, Guid> PublishedPriceVersion = new(
        id => id.Value, value => new PublishedPriceVersionId(value));

    /// <summary>`25-43`: <see cref="PriceKey"/> is a plain string wrapper, the identical
    /// non-nullable-<see cref="ModuleKey"/> shape above - listed here for the same reason.</summary>
    public static readonly ValueConverter<PriceKey, string> PriceKey = new(key => key.Value, value => new PriceKey(value));
}
