namespace Ago.Chat.Domain;

/// <summary>
/// `23-07`: the two beacon events `POST /api/v1/widget-activity` accepts on the wire, as
/// <c>"load"</c>/<c>"open"</c> (lower-case, matching the request body a browser sends - see
/// `WidgetActivityEndpoints.TryParseKind` for the one place that string is read). Deliberately not
/// a third <c>Conversation</c> member: the item's own Scope is explicit that a conversation is
/// counted "from the existing write path, never from a second beacon" - <see
/// cref="Application.Abstractions.IWidgetActivityRecorder.RecordConversation"/> is called directly
/// from <c>VisitorHub.JoinCoreAsync</c>, never through this endpoint or this enum.
/// </summary>
public enum WidgetActivityEventKind
{
    Load,
    Open,
}
