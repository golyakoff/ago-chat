using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;

/// <summary>What the console's <c>VisitorPanel</c> needs to render one row and offer an "unlink" action
/// - never the full <see cref="ChannelIdentity"/> aggregate, matching every other read-facing DTO in
/// this codebase.</summary>
public sealed record ChannelIdentitySummary(
    Guid ChannelIdentityId, ChannelKind Kind, string Address, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
