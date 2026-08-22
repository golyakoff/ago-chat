using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveOperatorIdentity;

public class ResolveOperatorIdentityHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenAnOperatorMatchesTheExternalSubjectId_ReturnsItsIdAndSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new FakeOperatorRepository();
        repository.Seed(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "keycloak-sub-123"));
        var handler = new ResolveOperatorIdentityHandler(repository);

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("keycloak-sub-123"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(operatorId, result.OperatorId);
        Assert.Equal(siteId, result.SiteId);
    }

    [Fact]
    public async Task HandleAsync_WhenNoOperatorMatches_ReturnsNull()
    {
        var handler = new ResolveOperatorIdentityHandler(new FakeOperatorRepository());

        var result = await handler.HandleAsync(new ResolveOperatorIdentityQuery("unknown-sub"), CancellationToken.None);

        Assert.Null(result);
    }
}
