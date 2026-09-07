using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetChannelCredentialStatus;

/// <summary>
/// `23-36`: the channel-neutral half of "does this site have a channel connected" - permission-checked
/// the same way <c>RegisterChannelCredential</c>/<c>RevokeChannelCredential</c> already are
/// (<see cref="Domain.Permission.ChannelManage"/>, per that permission's own doc comment: "there is no
/// separate read permission because... 'manage' already covers the one query this item ships"). What
/// this command deliberately does <em>not</em> do is ask the provider anything - that call is
/// provider-shaped (`adr/0006`'s "largest common denominator"), so it stays in the host, the same split
/// <c>RegisterChannelCredentialHandler</c>/<c>TelegramChannelEndpoints</c> already draw for
/// registration. This handler answers only "is there an active credential, and since when" - the host
/// then decides, per channel, whether and how to ask the provider whether that credential still works.
/// </summary>
public sealed record GetChannelCredentialStatus(OperatorId RequestedBy, SiteId SiteId, ChannelKind Kind);

/// <summary>
/// `23-36`: deliberately two fields and nothing shaped like a secret - the same "there is no field it
/// could even be assigned to" guarantee <see cref="RegisterChannelCredential.RegisteredChannelCredential"/>
/// already makes for the webhook secret's sibling case, restated here for the read side `adr/0069`'s own
/// "the console never reads it back" note anticipated but never gave a concrete type to. Not connected
/// is <see langword="null"/> in both fields, never a separate boolean - a caller who checks
/// <c><see cref="ChannelCredentialId"/> is not null</c> gets "connected" for free from the one fact that
/// already has to be true.
/// </summary>
public sealed record ChannelCredentialStatus(ChannelCredentialId? ChannelCredentialId, DateTimeOffset? CreatedAt);
