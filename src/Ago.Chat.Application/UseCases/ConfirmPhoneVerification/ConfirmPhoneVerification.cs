using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ConfirmPhoneVerification;

public sealed record ConfirmPhoneVerificationAsVisitor(
    ConversationId ConversationId, VisitorId RequestedBy, PendingPhoneVerificationId PendingPhoneVerificationId, string Code);

public sealed record ConfirmedPhoneVerification(Guid ChannelIdentityId, bool WasNewlyLinked);
