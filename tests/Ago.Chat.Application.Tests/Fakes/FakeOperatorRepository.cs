using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorRepository : IOperatorRepository
{
    private readonly Dictionary<string, Operator> _byExternalSubjectId = [];

    public Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        Task.FromResult(_byExternalSubjectId.GetValueOrDefault(externalSubjectId));

    public void Seed(Operator @operator)
    {
        if (@operator.ExternalSubjectId is { } externalSubjectId)
        {
            _byExternalSubjectId[externalSubjectId] = @operator;
        }
    }
}
