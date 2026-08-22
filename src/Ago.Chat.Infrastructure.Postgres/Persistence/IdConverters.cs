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

    public static readonly ValueConverter<OperatorId?, Guid?> NullableOperator = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new OperatorId(value.Value) : (OperatorId?)null);

    public static readonly ValueConverter<AttachmentId?, Guid?> NullableAttachment = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new AttachmentId(value.Value) : (AttachmentId?)null);

    public static readonly ValueConverter<MessageId?, Guid?> NullableMessage = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new MessageId(value.Value) : (MessageId?)null);
}
