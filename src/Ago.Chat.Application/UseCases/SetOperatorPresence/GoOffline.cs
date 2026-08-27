using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetOperatorPresence;

/// <summary>`4-06`: see <see cref="GoOnline"/>'s own remarks - the mirror image, called only when
/// <c>HubConnectionRegistration.OnDisconnectedAsync</c> reports this operator's last connection is gone.</summary>
public sealed record GoOffline(OperatorId OperatorId);
