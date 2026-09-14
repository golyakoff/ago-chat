namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `25-98`: codes <see cref="ErrorCodeCatalog"/> finds that `ErrorExtensions.cs`'s own switch
/// deliberately gives no line to, each with the reason - the same "an escape hatch that takes no
/// argument gets used" shape <see cref="MessageOpacityExemptions"/> already establishes for a
/// different rule. Mapping every one of these to *some* status would not make the code more correct;
/// none of them ever reaches <c>ErrorExtensions.ToProblem</c>; a status chosen for a call that never
/// happens cannot be observed, right or wrong, by anything - the same "untestable, therefore not a
/// claim worth making" reasoning `ErrorExtensions.cs`'s own switch comment gives in full for each of
/// these.
///
/// <para><b>What legitimately belongs here:</b> a code whose only callers are outside this method's
/// reach entirely (a SignalR hub, which translates through <c>HubException</c> directly and never
/// calls `ToProblem`) or a code with no caller at all. What does <i>not</i> belong here is a code that
/// reaches a real HTTP endpoint and simply has not been given a line yet - that is exactly the gap
/// this item's own audit exists to close, and the remedy is a line in the switch, not an entry
/// here.</para>
/// </summary>
internal static class ErrorCodeMappingExemptions
{
    private const string HubOnly =
        "Never reaches ErrorExtensions.ToProblem - VisitorHub/OperatorHub translate every Result failure "
        + "through HubException(error.Message) directly, and SignalR has no IResult/ToProblem pipeline to run "
        + "through. Confirmed by reading every caller of the factory method, not assumed from the code's name.";

    /// <summary>Keyed by the exact error code, which is already this codebase's own stable,
    /// globally-unique vocabulary (api-design.md) - unlike <see cref="MessageOpacityExemptions"/>'s
    /// compound key, a second code cannot collide with this one by accident.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Message.InvalidBody"] = HubOnly,
            ["Message.InvalidContent"] = HubOnly,
            ["Message.Unavailable"] = HubOnly
                + " (Also constructed by MessageBatchWriter/MessagePipelineWorkerHost/ChannelMessagePipeline, "
                + "all Infrastructure/Module-layer pipeline code with no Api caller either.)",
            ["Conversation.CreateRateLimited"] = HubOnly + " (StartConversationHandler's own only Api caller "
                + "is VisitorHub.)",
            ["TeamChat.Forbidden"] = HubOnly + " (OperatorHub only.)",
            ["TeamChat.InvalidBody"] = HubOnly + " (OperatorHub only.)",
            ["TeamChat.NotFound"] = HubOnly + " (OperatorHub only.)",
        };

    public static bool IsExempt(string code) => ByCode.ContainsKey(code);
}
