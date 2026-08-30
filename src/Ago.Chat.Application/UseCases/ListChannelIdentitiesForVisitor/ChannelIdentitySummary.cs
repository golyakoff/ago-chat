using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;

/// <summary>What the console's <c>VisitorPanel</c> needs to render one row and offer "unlink"/"prefer"
/// actions - never the full <see cref="ChannelIdentity"/> aggregate, matching every other read-facing
/// DTO in this codebase.
///
/// <para><c>IsPreferred</c> (`14-13`): whether this row is the visitor's own
/// <see cref="Visitor.PreferredChannelIdentityId"/> - always <see langword="false"/> for every row when
/// the visitor has none set, which is exactly the radio group's own "nothing selected yet" starting
/// state the backlog item asks for.</para></summary>
public sealed record ChannelIdentitySummary(
    Guid ChannelIdentityId, ChannelKind Kind, string Address, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
    bool IsPreferred);
