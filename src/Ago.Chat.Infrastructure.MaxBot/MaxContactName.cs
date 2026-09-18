namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `25-151`: MAX's own contact info carries first/last name as two separate, both-optional fields
/// (<see cref="MaxContactInfo"/>'s own honesty note) - joined here into the single <c>Name</c>
/// <c>VisitorContactDetail</c> value every other writer of that kind already produces (the widget's own
/// name field, `25-62`), rather than teaching <c>RecordChannelVisitorContact</c> a two-part name shape
/// nothing else in this codebase has. Shared by both of this channel's own inbound mechanisms
/// (<see cref="MaxLongPollingService"/>/<c>MaxWebhookEndpoints</c>) so the two can never join the two
/// halves differently. Public, not <see langword="internal"/> - <c>MaxWebhookEndpoints</c> is the one
/// caller sitting in a different assembly (<c>Ago.Chat.Api</c>), the same "this channel's own vocabulary
/// stays below Infrastructure, but Infrastructure's own public surface reaches every host that wires it
/// up" shape this file's own siblings (<c>MaxApiClient</c>, <c>MaxChannelAdapter</c>) already have.
/// </summary>
public static class MaxContactName
{
    public static string? Compose(string? firstName, string? lastName)
    {
        var parts = new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part));
        var name = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
