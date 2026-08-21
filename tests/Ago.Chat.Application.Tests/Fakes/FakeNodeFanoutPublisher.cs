using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeNodeFanoutPublisher : INodeFanoutPublisher
{
    public sealed record Call(IReadOnlyCollection<PrincipalKey> Recipients, string Method, string PayloadJson, Guid CorrelationId);

    public List<Call> Calls { get; } = [];

    public Task PublishAsync(
        IReadOnlyCollection<PrincipalKey> recipients, string method, string payloadJson, Guid correlationId, CancellationToken cancellationToken)
    {
        Calls.Add(new Call(recipients, method, payloadJson, correlationId));
        return Task.CompletedTask;
    }
}
