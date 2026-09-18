using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordChannelVisitorContact;

/// <summary>
/// What an adapter gets back once a shared contact has become an ordinary
/// <see cref="VisitorContactDetail"/>. <paramref name="NameRecorded"/> is <see langword="false"/> both
/// when the channel gave no name at all and when a name was given but its own write failed - this
/// handler's own remarks on why that failure never undoes the phone write above it.
/// </summary>
public sealed record RecordChannelVisitorContactResult(VisitorId VisitorId, ConversationId ConversationId, bool NameRecorded);
