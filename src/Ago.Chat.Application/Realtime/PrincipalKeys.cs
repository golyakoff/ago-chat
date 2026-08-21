using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Realtime;

/// <summary>
/// The one place chat decides how its own identities map onto the platform's opaque
/// <see cref="PrincipalKey"/> (realtime.md's <c>presence:visitor:{id}</c>/<c>presence:operator:{id}</c>
/// schema - <see cref="Ago.Platform.Abstractions.IConnectionRegistry"/> never knows a visitor or an
/// operator exists, per clean-architecture.md's qualifying rule). Lives in Application, not
/// <c>Ago.Chat.Api</c>, because <c>Ago.Chat.Worker</c> needs the exact same mapping to resolve a
/// conversation's participants when it fans messages out across nodes (3-02) - two hand-written
/// copies of a string prefix is exactly the kind of drift a shared helper exists to prevent.
/// </summary>
public static class PrincipalKeys
{
    public static PrincipalKey ForVisitor(VisitorId visitorId) => new($"visitor:{visitorId.Value}");

    public static PrincipalKey ForOperator(OperatorId operatorId) => new($"operator:{operatorId.Value}");
}
