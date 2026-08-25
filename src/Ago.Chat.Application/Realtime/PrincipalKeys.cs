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
    public const string VisitorKind = "visitor";

    public const string OperatorKind = "operator";

    /// <summary>A key this class did not build. Never expected in practice - the constant exists so
    /// the metric tag stays bounded (three values, forever) instead of growing a time series per
    /// unrecognised key.</summary>
    public const string UnknownKind = "unknown";

    private const string VisitorPrefix = $"{VisitorKind}:";

    private const string OperatorPrefix = $"{OperatorKind}:";

    public static PrincipalKey ForVisitor(VisitorId visitorId) => new($"{VisitorPrefix}{visitorId.Value}");

    public static PrincipalKey ForOperator(OperatorId operatorId) => new($"{OperatorPrefix}{operatorId.Value}");

    /// <summary>`7-08`: reads back what the two methods above wrote - which kind of principal a key
    /// names, without the id. The one dimension that makes a fan-out's "reached nobody" worth
    /// looking at: a visitor who closed the tab is ordinary, an operator with no connection is not.
    /// It lives here, next to the code that builds the keys, for exactly the reason this class
    /// exists at all - a second hand-written copy of the prefix is what drifts.</summary>
    public static string KindOf(PrincipalKey key) => key.Value switch
    {
        var value when value.StartsWith(VisitorPrefix, StringComparison.Ordinal) => VisitorKind,
        var value when value.StartsWith(OperatorPrefix, StringComparison.Ordinal) => OperatorKind,
        _ => UnknownKind,
    };
}
