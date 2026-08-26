using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorRepository : IOperatorRepository
{
    private readonly Dictionary<string, Operator> _byExternalSubjectId = [];
    private readonly List<Operator> _all = [];

    public Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        Task.FromResult(_byExternalSubjectId.GetValueOrDefault(externalSubjectId));

    /// <summary>`14-04`: mirrors <c>OperatorRepository</c>'s own predicate exactly - <c>Online</c>
    /// only, no capacity term. A fake that quietly used a different rule than the adapter would let a
    /// test pass against a condition production does not implement.</summary>
    public Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Exists(o => o.SiteId == siteId && o.Status == OperatorStatus.Online));

    public void Seed(Operator @operator)
    {
        _all.Add(@operator);
        if (@operator.ExternalSubjectId is { } externalSubjectId)
        {
            _byExternalSubjectId[externalSubjectId] = @operator;
        }
    }
}
