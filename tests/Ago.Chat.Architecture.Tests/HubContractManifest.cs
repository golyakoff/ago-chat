namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `5-19`: every hub method a client can invoke, and how many arguments it takes.
///
/// <para><b>Why a checked-in list rather than a rule computed from the code.</b> "A parameter list
/// must not grow" is not a property of one snapshot - it is a comparison against what shipped, and
/// nothing in the assembly remembers that. So the baseline lives here, in a file somebody has to
/// edit, and <see cref="HubContractTests"/> compares reflection against it. Editing a number here is
/// the moment a reviewer gets to ask "which deployed client did you just break?", which is exactly
/// the question nobody was asked when `14-06` went from four parameters to seven.</para>
///
/// <para><b>Adding a method is also a change this list notices</b>, and deliberately: a new entry is
/// cheap to add and carries the one fact worth recording, which is whether anything is already
/// calling it. That is why each entry says.</para>
///
/// <para><b>The lifecycle overrides are not here.</b> <c>OnConnectedAsync</c>/
/// <c>OnDisconnectedAsync</c> are called by SignalR itself, never by a client invocation, so their
/// signatures are ASP.NET Core's contract rather than ours - <see cref="HubContractTests"/> filters
/// them out rather than listing them.</para>
/// </summary>
internal static class HubContractManifest
{
    /// <summary>One invokable hub method: its arity, and who is already calling it.</summary>
    internal sealed record HubMethod(string Key, int Arity, string CalledBy);

    /// <summary>
    /// Keyed <c>Hub.Method</c>. Arities are what a deployed client sends today, not what the C#
    /// signature could tolerate: SignalR requires one argument per declared parameter and does not
    /// fall back to a C# default, so an optional parameter is optional to a C# caller and mandatory
    /// to a wire caller.
    /// </summary>
    public static readonly IReadOnlyList<HubMethod> Methods =
    [
        new("VisitorHub.JoinAsync", 1,
            "ago-widget VisitorConnection (`5-07` resume); dev-harness.html"),
        new("VisitorHub.JoinWithTrafficSourceAsync", 5,
            "ago-widget VisitorConnection.start (`18-12`) - the widget's own real first-open join, "
            + "carrying the referrer host and the three UTM parameters it read from the browser. "
            + "JoinAsync above stays exactly as it was (the resume path never re-sends a source), "
            + "the same split `5-19` already made for SendMessageAsync/SendStructuredMessageAsync."),
        new("VisitorHub.SendMessageAsync", 4,
            "ago-widget VisitorConnection - THE method `14-06` broke and `5-19` restored. Four is not "
            + "negotiable: every widget already embedded on somebody else's site sends exactly this."),
        new("VisitorHub.SendStructuredMessageAsync", 7,
            "nobody yet - `5-19` created it so `14-06`'s envelope has somewhere to live that is not "
            + "SendMessageAsync. `20-06`/`21-01` are the expected first callers."),
        new("VisitorHub.GetHistoryAsync", 3,
            "ago-widget VisitorConnection; dev-harness.html"),

        new("OperatorHub.JoinConversationAsync", 2,
            "ago-console OperatorConnection"),
        new("OperatorHub.SendMessageAsync", 4,
            "ago-console OperatorConnection (`5-16`). Same rule as the visitor side."),
        new("OperatorHub.SendStructuredMessageAsync", 7,
            "nobody yet - the mirror of the visitor side's, kept symmetrical since `5-07`."),
        new("OperatorHub.GetHistoryAsync", 3,
            "ago-console OperatorConnection"),
        new("OperatorHub.GetVisitorPresenceAsync", 1,
            "ago-console OperatorConnection (`5-14`)"),
        new("OperatorHub.GetVisitorHistoryConversationAsync", 4,
            "ago-console OperatorConnection.getVisitorHistoryConversation (`18-07`) - the "
            + "returning-visitor-history panel's own \"open one\", not a caller of GetHistoryAsync "
            + "above: the authorization rule genuinely differs (assigned to *a* live conversation with "
            + "this visitor, not to the specific historical one being read)."),
    ];
}
