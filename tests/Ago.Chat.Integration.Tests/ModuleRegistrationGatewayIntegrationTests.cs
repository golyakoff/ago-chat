using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-11`: <see cref="HttpModuleRegistrationGateway"/> over a real HTTP round trip against a real, in
/// process Kestrel host - the same "does this boundary's own adapter translate correctly" bar
/// <c>ModuleTaskGatewayIntegrationTests</c> already sets for its own sibling gateway, narrowed the
/// identical way: no Postgres, no RabbitMQ, the gateway used directly and unwrapped.
/// </summary>
public class ModuleRegistrationGatewayIntegrationTests
{
    private static readonly ModuleKey Calendar = new("calendar");
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleProvisioningSecret ProvisioningSecret = new("a-provisioning-secret-of-sixteen-plus-chars");

    [Fact]
    public async Task RegisterAsync_SendsTheProvisioningSecretHeader_AndTheCredentialInTheBody()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        await gateway.RegisterAsync(target, new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), ProvisioningSecret, CancellationToken.None);

        var received = Assert.Single(server.ReceivedRegisterRequests);
        Assert.Equal(ProvisioningSecret.Value, received.ProvisioningSecretHeader);
        Assert.Equal("a-shared-secret-of-sixteen-plus-chars", received.Credential);
        Assert.Equal($"/api/v1/module-registrations/{SiteId.Value}", received.Path);
    }

    [Fact]
    public async Task RotateAsync_SendsTheNewCredential_ToTheRotateRoute()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        await gateway.RotateAsync(target, new ModuleCredential("rotated-secret-of-sixteen-plus-chars-x"), ProvisioningSecret, CancellationToken.None);

        var received = Assert.Single(server.ReceivedRotateRequests);
        Assert.Equal("rotated-secret-of-sixteen-plus-chars-x", received.NewCredential);
        Assert.Equal($"/api/v1/module-registrations/{SiteId.Value}/rotate", received.Path);
    }

    [Fact]
    public async Task RevokeAsync_CallsTheDeleteRoute_WithTheProvisioningSecretHeader()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        await gateway.RevokeAsync(target, ProvisioningSecret, CancellationToken.None);

        var received = Assert.Single(server.ReceivedRevokeRequests);
        Assert.Equal(ProvisioningSecret.Value, received);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesTheModulesStatusResponse()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();
        server.StatusResponseJson = """{"exists":true,"registeredAt":"2026-01-01T12:00:00+00:00","hasCredentialInGracePeriod":true}""";

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        var status = await gateway.GetStatusAsync(target, ProvisioningSecret, CancellationToken.None);

        Assert.True(status.Exists);
        Assert.True(status.HasCredentialInGracePeriod);
    }

    /// <summary>`22-11`'s own translation rule: a wrong provisioning secret (a 401 from the module) is
    /// reported the identical way any other unreachable failure is - see
    /// <see cref="IModuleRegistrationGateway"/>'s own remarks for why one exception type serves every
    /// underlying cause.</summary>
    [Fact]
    public async Task RegisterAsync_WhenTheModuleRefuses_ThrowsModuleUnreachableException()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();
        server.RefuseEveryCall = true;

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        await Assert.ThrowsAsync<ModuleUnreachableException>(() => gateway.RegisterAsync(
            target, new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), ProvisioningSecret, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsync_WhenTheModuleIsUnreachable_ThrowsModuleUnreachableException()
    {
        await using var server = new FakeModuleRegistrationServer();
        await server.StartAsync();
        await server.StopAsync();

        var gateway = new HttpModuleRegistrationGateway(new HttpClient());
        var target = new ModuleRegistrationTarget(Calendar, SiteId, server.BaseAddress);

        await Assert.ThrowsAsync<ModuleUnreachableException>(() => gateway.RegisterAsync(
            target, new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), ProvisioningSecret, CancellationToken.None));
    }

    /// <summary>A minimal Kestrel host answering the generic registration contract - the identical
    /// "not the real product's own routes" fake <c>ModuleTaskGatewayIntegrationTests.FakeModuleServer</c>
    /// already establishes for the sibling gateway.</summary>
    private sealed class FakeModuleRegistrationServer : IAsyncDisposable
    {
        private WebApplication? _app;

        public Uri BaseAddress { get; private set; } = null!;

        public bool RefuseEveryCall { get; set; }

        public string StatusResponseJson { get; set; } = """{"exists":false,"registeredAt":null,"hasCredentialInGracePeriod":false}""";

        public List<(string Path, string ProvisioningSecretHeader, string Credential)> ReceivedRegisterRequests { get; } = [];

        public List<(string Path, string NewCredential)> ReceivedRotateRequests { get; } = [];

        public List<string> ReceivedRevokeRequests { get; } = [];

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.MapPut("/api/v1/module-registrations/{siteId}", async context =>
            {
                if (RefuseEveryCall)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                using var reader = new StreamReader(context.Request.Body);
                var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                lock (ReceivedRegisterRequests)
                {
                    ReceivedRegisterRequests.Add((
                        context.Request.Path,
                        context.Request.Headers["X-Ago-Module-Provisioning-Secret"].ToString(),
                        body.RootElement.GetProperty("credential").GetString()!));
                }
            });

            app.MapPost("/api/v1/module-registrations/{siteId}/rotate", async context =>
            {
                if (RefuseEveryCall)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                using var reader = new StreamReader(context.Request.Body);
                var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                lock (ReceivedRotateRequests)
                {
                    ReceivedRotateRequests.Add((context.Request.Path, body.RootElement.GetProperty("newCredential").GetString()!));
                }
            });

            app.MapDelete("/api/v1/module-registrations/{siteId}", context =>
            {
                if (RefuseEveryCall)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                lock (ReceivedRevokeRequests)
                {
                    ReceivedRevokeRequests.Add(context.Request.Headers["X-Ago-Module-Provisioning-Secret"].ToString());
                }

                return Task.CompletedTask;
            });

            app.MapGet("/api/v1/module-registrations/{siteId}", async context =>
            {
                if (RefuseEveryCall)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(StatusResponseJson);
            });

            await app.StartAsync();
            _app = app;

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            BaseAddress = new Uri(addresses.Addresses.First());
        }

        public async Task StopAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
