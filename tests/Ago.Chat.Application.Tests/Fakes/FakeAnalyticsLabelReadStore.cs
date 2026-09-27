using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-237`: a seedable <see cref="IAnalyticsLabelReadStore"/> for the analytics report handler
/// tests. It records the ids each report asked to resolve (so a test can prove the handler queried only
/// the unresolved ones, or none at all on the live path) and returns whatever names it was seeded with -
/// an id it was never seeded with is simply absent from the map, exactly as the real store omits an id
/// with no row.</summary>
public sealed class FakeAnalyticsLabelReadStore : IAnalyticsLabelReadStore
{
    private readonly Dictionary<OperatorId, string> _operatorNames = [];
    private readonly Dictionary<TagId, string> _tagNames = [];

    public List<IReadOnlyCollection<OperatorId>> OperatorLookups { get; } = [];

    public List<IReadOnlyCollection<TagId>> TagLookups { get; } = [];

    public FakeAnalyticsLabelReadStore SeedOperator(OperatorId id, string name)
    {
        _operatorNames[id] = name;
        return this;
    }

    public FakeAnalyticsLabelReadStore SeedTag(TagId id, string name)
    {
        _tagNames[id] = name;
        return this;
    }

    public Task<IReadOnlyDictionary<OperatorId, string>> GetOperatorDisplayNamesAsync(
        SiteId siteId, IReadOnlyCollection<OperatorId> operatorIds, CancellationToken cancellationToken)
    {
        OperatorLookups.Add(operatorIds);
        IReadOnlyDictionary<OperatorId, string> result = operatorIds
            .Where(_operatorNames.ContainsKey)
            .ToDictionary(id => id, id => _operatorNames[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<TagId, string>> GetTagNamesAsync(
        SiteId siteId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken)
    {
        TagLookups.Add(tagIds);
        IReadOnlyDictionary<TagId, string> result = tagIds
            .Where(_tagNames.ContainsKey)
            .ToDictionary(id => id, id => _tagNames[id]);
        return Task.FromResult(result);
    }
}
