namespace Ago.Chat.Contracts;

/// <summary>The realtime protocol's wire shape for `25-119`'s live push, pushed to the one operator
/// connection that authored the delivered message, as <c>"MessageDelivered"</c> - the same
/// single-fact-push pattern <see cref="AttachmentUploadGrantChangedDto"/> already establishes.
/// <see cref="ConversationId"/>/<see cref="MessageId"/> together are what let the console's
/// <c>Thread.tsx</c> find the one message row to flip its badge on, the same pair
/// <see cref="MessageDto"/>'s own <c>ConversationId</c>/<c>Id</c> already identify a message by.</summary>
public sealed record MessageDeliveredDto(Guid ConversationId, Guid MessageId, DateTimeOffset DeliveredAt);
