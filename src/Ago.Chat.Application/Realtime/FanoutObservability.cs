using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Realtime;

/// <summary>
/// `7-08`: turns a <see cref="FanoutResult"/> - the platform's domain-free report of who a fan-out
/// was meant for and how many live connections each had - into the one instrument that can tell an
/// ordinary "reached nobody" from an interesting one.
///
/// The dimensioning is the decision this item exists to make (`adr/0044`), and it is two tags, not a
/// raw count: **recipient kind**, because a visitor who closed the tab is the expected outcome many
/// times a day while an operator with no connection is not, and **presence**, because "the registry
/// knew of nobody" and "the registry knew of somebody" are the two cases that need telling apart
/// before anything downstream can. No alert is defined here on purpose - `15-03` decides that with
/// real data rather than with a guess made while adding the instrument.
///
/// Lives in Application, next to <see cref="PrincipalKeys"/>, because both fan-out handlers need
/// exactly this and a second hand-written copy is what drifts. Calling
/// <c>System.Diagnostics.Metrics</c> from Application is the same call already settled for
/// <c>ILogger</c> (see Ago.Chat.Application.csproj's own remarks): a cross-cutting diagnostic API
/// with no I/O of its own, a no-op until a host wires an exporter to it - not an external resource,
/// so clean-architecture.md's "every external resource sits behind a port" does not reach it.
/// </summary>
public static class FanoutObservability
{
    /// <param name="result">What <see cref="INodeFanoutPublisher.PublishAsync"/> resolved.</param>
    /// <param name="method">The wire method being fanned out ("MessageReceived",
    /// "ConversationAssigned") - bounded, and what separates a message's fan-out from an assignment
    /// notification's on a dashboard.</param>
    public static void RecordFanout(FanoutResult result, string method)
    {
        foreach (var recipient in result.Recipients)
        {
            ChatMetrics.RecordDeliveryRecipient(
                method,
                PrincipalKeys.KindOf(recipient.Recipient),
                hadLiveConnection: recipient.Connections > 0);
        }
    }
}
