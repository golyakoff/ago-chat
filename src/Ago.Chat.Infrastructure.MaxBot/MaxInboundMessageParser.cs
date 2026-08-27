namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: the one place a <see cref="MaxUpdate"/> becomes something worth acting on - a pure function,
/// used identically by the webhook receiver (<c>Ago.Chat.Api</c>'s <c>MaxWebhookEndpoints</c>) and the
/// long-polling loop (<see cref="MaxLongPollingService"/>), so the two inbound mechanisms this item ships
/// cannot disagree about what a message is.
///
/// <para>Recognises only <c>update_type == "message_created"</c> - MAX's own envelope carries other
/// event kinds (a bot being started, a chat's title changing) that this item has no use case for; every
/// other kind, and any update whose payload does not match the expected shape, returns
/// <see langword="null"/> rather than throwing, which is what lets a caller "skip the ones we do not
/// understand and keep going" instead of one malformed update stalling either loop.</para>
/// </summary>
public static class MaxInboundMessageParser
{
    private const string MessageCreatedUpdateType = "message_created";

    public static ParsedMaxMessage? TryParse(MaxUpdate update)
    {
        if (update.UpdateType != MessageCreatedUpdateType)
        {
            return null;
        }

        if (update.Message?.Sender?.UserId is not { } senderId)
        {
            return null;
        }

        var text = update.Message.Body?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (14-01's own design) - a fallback synthesised from sender+timestamp is used only
        // if MAX ever omits `body.mid`, which nothing in the public documentation confirms it does or
        // does not do. Recorded here rather than assumed away: a real captured payload is what should
        // remove this fallback or confirm it is dead code.
        var externalMessageId = update.Message.Body?.Mid
            ?? $"{senderId}:{update.Message.Timestamp ?? update.Timestamp ?? 0}";

        return new ParsedMaxMessage(senderId, externalMessageId, text);
    }
}

public sealed record ParsedMaxMessage(long SenderId, string ExternalMessageId, string Text);
