using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetOperatorPresence;

/// <summary>`4-06`: no `SiteId` - unlike every other command in this codebase, presence is a property
/// of the operator row itself, not of one site relationship, and the caller (`OperatorHub`) already
/// resolved a single <see cref="Domain.OperatorId"/> from the connection's own JWT.</summary>
public sealed record GoOnline(OperatorId OperatorId);
