using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-03`'s own Done-when, proven end to end against a real Postgres and the real AES-GCM cipher
/// (not fakes) - "create returns the secret exactly once and a subsequent GET never includes it,"
/// "delete flips `active` to false... stays queryable for its own delivery history," and
/// `webhook:manage` enforcement.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookRegistrationFlowTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly WebhookSecretCipherOptions CipherOptions = new()
    {
        SecretEncryptionKey = "Vg1G2KjonUB1uH8trETJzr30EPoeqt0YRGzYibDKy1o=",
    };

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorWithWebhookManage()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.WebhookManage.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    private RegisterWebhookEndpointHandler CreateRegisterHandler(AgoChatDbContext db) => new(
        new WebhookEndpointRepository(db), new PermissionChecker(db), new WebhookSecretGenerator(),
        new WebhookSecretCipher(CipherOptions), new UuidV7Generator(), new FixedClock(Now));

    [Fact]
    public async Task RegisterThenList_TheSecretIsReturnedOnlyOnCreate_NeverOnList()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorWithWebhookManage();

        await using var registerDb = fixture.CreateDbContext();
        var registerResult = await CreateRegisterHandler(registerDb).HandleAsync(
            new RegisterWebhookEndpoint(operatorId, siteId, new Uri("https://shop.example.com/hooks/ago")), CancellationToken.None);

        Assert.True(registerResult.IsSuccess);
        Assert.StartsWith("whsec_", registerResult.Value.Secret);

        await using var listDb = fixture.CreateDbContext();
        var listHandler = new ListWebhookEndpointsHandler(new WebhookEndpointRepository(listDb), new PermissionChecker(listDb));
        var listResult = await listHandler.HandleAsync(new ListWebhookEndpoints(operatorId, siteId), CancellationToken.None);

        Assert.True(listResult.IsSuccess);
        var dto = Assert.Single(listResult.Value.Endpoints);
        Assert.Equal(registerResult.Value.WebhookEndpointId, dto.Id);
        Assert.True(dto.Active);
        // WebhookEndpointDto (Ago.Chat.Contracts) has no property that could carry a secret - there is
        // nothing to assert null on, which is the actual guarantee: the wire type structurally cannot.

        // And the row at rest never holds the plaintext either - only its AES-GCM ciphertext
        // (WebhookSecretCipherTests covers the round-trip and no-plaintext-leak properties directly).
        await using var rawDb = fixture.CreateDbContext();
        var stored = await new WebhookEndpointRepository(rawDb).GetByIdAsync(
            new WebhookEndpointId(registerResult.Value.WebhookEndpointId), CancellationToken.None);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(registerResult.Value.Secret), stored!.SecretCiphertext);
    }

    [Fact]
    public async Task RegisterThenRevoke_DeliveryHistoryStaysQueryable()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorWithWebhookManage();

        await using var registerDb = fixture.CreateDbContext();
        var registerResult = await CreateRegisterHandler(registerDb).HandleAsync(
            new RegisterWebhookEndpoint(operatorId, siteId, new Uri("https://shop.example.com/hooks/ago")), CancellationToken.None);
        var endpointId = new WebhookEndpointId(registerResult.Value.WebhookEndpointId);

        // A delivery row written directly at the repository level - this item's own scope: "actually
        // sending... is 6-05's job, this item's tests write webhook_deliveries rows directly."
        await using (var deliveryDb = fixture.CreateDbContext())
        {
            var delivery = WebhookDelivery.Record(
                new WebhookDeliveryId(new UuidV7Generator().NewId(Now)), endpointId, "message.created", "{}",
                attempt: 1, WebhookDeliveryStatus.Delivered, 200, "OK", Now, Now);
            await new WebhookDeliveryRepository(deliveryDb).SaveAsync(delivery, CancellationToken.None);
        }

        await using (var revokeDb = fixture.CreateDbContext())
        {
            var revokeHandler = new RevokeWebhookEndpointHandler(new WebhookEndpointRepository(revokeDb), new PermissionChecker(revokeDb));
            var revokeResult = await revokeHandler.HandleAsync(
                new RevokeWebhookEndpoint(endpointId, operatorId, siteId), CancellationToken.None);
            Assert.True(revokeResult.IsSuccess);
        }

        await using var readDb = fixture.CreateDbContext();
        var deliveriesHandler = new GetWebhookDeliveriesHandler(
            new WebhookEndpointRepository(readDb), new WebhookDeliveryReadStore(fixture.DataSource), new PermissionChecker(readDb));
        var deliveriesResult = await deliveriesHandler.HandleAsync(
            new GetWebhookDeliveries(endpointId, operatorId, siteId, null, 50), CancellationToken.None);

        Assert.True(deliveriesResult.IsSuccess);
        Assert.Single(deliveriesResult.Value.Deliveries);

        var stored = await new WebhookEndpointRepository(readDb).GetByIdAsync(endpointId, CancellationToken.None);
        Assert.False(stored!.Active);
    }

    [Fact]
    public async Task Register_WithoutWebhookManage_ReturnsForbidden()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        await using var db2 = fixture.CreateDbContext();
        var result = await CreateRegisterHandler(db2).HandleAsync(
            new RegisterWebhookEndpoint(operatorId, siteId, new Uri("https://shop.example.com/hooks/ago")), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
