using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeChannelCredential;

public sealed record RevokeChannelCredential(ChannelCredentialId ChannelCredentialId, OperatorId RequestedBy, SiteId SiteId);
