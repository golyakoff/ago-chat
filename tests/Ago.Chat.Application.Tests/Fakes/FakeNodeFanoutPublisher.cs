using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeNodeFanoutPublisher : INodeFanoutPublisher
{
    public sealed record Call(IReadOnlyCollection<PrincipalKey> Recipients, string Method, string PayloadJson, Guid CorrelationId);

    public List<Call> Calls { get; } = [];

    /// <summary>`7-08`: how many live connections the registry is pretending to have for a given
    /// principal. Anything not named here resolves to zero - the ordinary case (a visitor who closed
    /// the tab), and the one a test has to be able to express to prove the instrument tells it apart
    /// from a connected recipient.</summary>
    public Dictionary<PrincipalKey, int> ConnectionsByPrincipal { get; } = [];

    public Task<FanoutResult> PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients, string method, string payloadJson, Guid correlationId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(recipients, method, payloadJson, correlationId));
        var resolved = recipients
            .Select(recipient => new ResolvedRecipient(recipient, ConnectionsByPrincipal.GetValueOrDefault(recipient)))
            .ToArray();
        return Task.FromResult(new FanoutResult(resolved));
    }
}
