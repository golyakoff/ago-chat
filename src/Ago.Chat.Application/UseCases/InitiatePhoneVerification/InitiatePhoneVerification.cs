using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.InitiatePhoneVerification;

/// <summary>
/// Visitor-only, deliberately - see <see cref="InitiatePhoneVerificationHandler"/>'s own remarks for why
/// this item, unlike `CreateAttachment`/`RecordVisitorContactDetail`, was scoped without an
/// operator-initiated twin.
/// </summary>
public sealed record InitiatePhoneVerificationAsVisitor(ConversationId ConversationId, VisitorId RequestedBy, string Phone);

public sealed record InitiatedPhoneVerification(Guid PendingPhoneVerificationId, DateTimeOffset ExpiresAt, string DeliveryMethod);
