using Ago.Chat.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-07`: proves what nothing in this suite proved before - that every Minimal API route
/// <c>Program.cs</c> maps has a handler whose parameters all resolve from the real, composed
/// <see cref="IServiceProvider"/>, the way `25-06` found out the hard way that
/// <c>OperatorInviteEndpointTests</c> never actually checked (that file's own <c>TestHost</c> never
/// happened to be the first thing in its process to trigger the lazy, whole-app endpoint-metadata
/// build - `25-06`'s own report; `ChannelStatusEndpointsTests` independently rediscovered the same
/// trigger while proving three unrelated routes and left the fullest account of it in its own class
/// remarks).
///
/// <para><b>Why this composes through <see cref="CompositionRoot"/> rather than a third hand-rebuilt
/// subset.</b> `OperatorInviteEndpointTests` and `ChannelStatusEndpointsTests` each rebuild their own
/// slice of `Program.cs`'s registrations by hand - real production types, but a second, independently
/// maintained list that can drift from the first the moment someone adds a route and forgets the
/// mirror (exactly `25-06`'s own shape: `PreviewOperatorInviteHandler` was mapped in `Program.cs`, in
/// `23-70`, and never registered in `ChatModule.cs` - two places, one edited, one not). A test that
/// is itself a second hand-written list has the identical failure mode baked in on day one, just with
/// full rather than partial coverage. <see cref="CompositionRoot.ConfigureServices"/> and
/// <see cref="CompositionRoot.MapEndpoints"/> exist precisely so there is only one list:
/// `Program.cs` and this file both call the same two methods, so a route or a registration added to
/// one is automatically exercised by the other - not by discipline, by construction. See
/// `CompositionRoot`'s own remarks for why it exists as an extract-method refactor of `Program.cs`
/// rather than a second copy.</para>
///
/// <para><b>Why no Testcontainers, unlike every other file in this project (<c>OperatorOidcFixture</c>
/// and friends).</b> This item's own text asks, as the first thing to establish before writing any
/// check at all, whether ASP.NET Core's DI container can validate that every registered service's
/// dependency graph is satisfiable without opening a live connection to anything. Investigated
/// experimentally (not from documentation) against this exact codebase's own registrations:
/// <list type="bullet">
/// <item><description><c>AddPostgresPersistence</c> (`Ago.Chat.Infrastructure.Postgres`) calls
/// <c>new NpgsqlDataSourceBuilder(connectionString).Build()</c> eagerly, outside any DI factory -
/// but <c>NpgsqlDataSourceBuilder.Build()</c> only builds a connection-pool object; verified
/// experimentally (a throwaway console app pointed at `192.0.2.1` - a non-routable, RFC 5737
/// documentation address - returned in 46ms) that it opens no socket.</description></item>
/// <item><description><c>AddRedisCaching</c> (`Ago.Platform.Caching.Redis`) registers
/// <c>IConnectionMultiplexer</c> via <c>services.TryAddSingleton&lt;IConnectionMultiplexer&gt;(_ =&gt;
/// ConnectionMultiplexer.Connect(...))</c> - a real, blocking connection attempt, but only inside a
/// factory delegate, never invoked unless something actually resolves
/// <c>IConnectionMultiplexer</c>.</description></item>
/// <item><description><c>AddRabbitMqMessaging</c> (`Ago.Platform.Messaging.RabbitMq`) registers
/// <c>RabbitMqConnection</c> as a plain constructor-based singleton; its constructor only stores
/// `options`/`logger` - the real `IConnection` opens lazily inside `GetConnectionAsync`, called only
/// from `CreateChannelAsync`, never from construction.</description></item>
/// <item><description>Whether any of the three ever actually get constructed by
/// <c>BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true })</c> was itself
/// verified experimentally, not assumed: a throwaway service collection with a singleton registered
/// via a factory delegate that increments a counter and writes to console, built with
/// <c>ValidateOnBuild = true</c> against an otherwise fully-resolvable graph, left that counter at
/// zero. <c>ValidateOnBuild</c> walks each descriptor's own call site - "does a registration exist for
/// every constructor parameter, recursively" - and never invokes a factory delegate or a constructor
/// to do it. A second run, with one dependency deliberately left unregistered, confirmed the walk
/// still throws in that case (an `AggregateException` naming the exact missing type) - so the
/// structural check is real, not a check that happens to pass because it never looks.</description></item>
/// </list>
/// The one test below that does start the host for real
/// (<see cref="EveryMappedRouteHandlerParameterResolves_ReproducesThe25_06Crash"/>) still never opens a
/// live connection, for a second, independent reason: it calls <c>UseTestServer()</c> (no real socket)
/// and its own <see cref="BuildRealHost"/> never registers any <c>IHostedService</c> - the only code in
/// this whole graph that would ever actually call `ConnectionMultiplexer.Connect`,
/// `RabbitMqConnection.CreateChannelAsync`, or open a real Postgres command. `Program.cs`'s own hosted
/// services (`ConnectionHeartbeat`, `NodeDeliveryConsumer`, `MessagePipelineWorkerHost`, ...) are
/// deliberately outside `CompositionRoot.ConfigureServices` for exactly this reason - see that class's
/// own remarks.</para>
///
/// <para><b>Three config values have to be real-looking before any of this builds at all</b> -
/// <c>AGO_CHAT_CONNECTION_STRING</c> (an env var `ChatModule.ConfigureServices` reads with `?? throw`)
/// and `Auth:Keycloak:Authority` (a config key `CompositionRoot.ConfigureServices` reads the same way),
/// both synchronous guards evaluated immediately, not through `IOptions&lt;T&gt;` - plus one this class
/// found only by running the test and reading the exception: `AddPlatformObservability` (`Ago.Platform.Observability`)
/// deliberately validates its own `Otel:Exporter:Endpoint` synchronously too, "right here" rather than
/// deferred to `ValidateOnStart`, by its own class remarks - so `[Required] Uri? Endpoint` has to be set
/// and URI-shaped before `CompositionRoot.ConfigureServices` returns at all. Every other
/// required-looking setting in this codebase is bound through `AddOptions&lt;T&gt;().Bind(...)`, which is
/// itself lazy (registers a configure-callback that only runs when `IOptions&lt;T&gt;.Value` is first
/// read) - confirmed by grepping both `ChatModule.cs` and `Program.cs`'s own extracted body for every
/// other `?? throw`/`GetEnvironmentVariable`/direct `configuration[...]` read: there are none. None of
/// the three fake values below is ever dialled - `192.0.2.1` is RFC 5737's documentation-only address,
/// guaranteed never to answer.</para>
/// </summary>
public sealed class RouteHandlerDiRegistrationTests
{
    private const string FakeConnectionString =
        "Host=192.0.2.1;Port=1;Database=unreachable;Username=nobody;Password=nobody;Timeout=1";

    private const string FakeKeycloakAuthority = "http://192.0.2.1:1/realms/unreachable";

    /// <summary>Below this, something is silently mapping fewer routes than production does - see
    /// this class's own remarks on why a route-count floor, not a hand-maintained route list, is this
    /// test's own coverage guarantee (`CompositionRoot.MapEndpoints` is the one and only route list,
    /// shared with `Program.cs` itself). This suite's own real, observed count the day this test was
    /// written was 168 (`RouteHandlerDiRegistrationTests`, `25-07`); 150 leaves an 18-route margin -
    /// not tuned tight, because the point is to catch "a whole route group stopped being mapped",
    /// not to track the exact count release to release.</summary>
    private const int MinimumExpectedRouteCount = 150;

    [Fact]
    public void EveryRegisteredServiceConstructorDependencyResolves_StructuralOnly_NoLiveConnection()
    {
        var services = BuildRealServiceCollection();

        // ValidateOnBuild throws the moment any registered service's own constructor needs a type
        // nothing in this exact collection provides - see this class's own remarks for the
        // experimental proof that this check is structural only (no factory delegate, no constructor,
        // ever actually runs). This is the broader, complementary net: it catches "service A's
        // constructor needs service B, and B was never registered" - a different shape from `25-06`'s
        // own bug, which is why EveryMappedRouteHandlerParameterResolves below exists as well, not
        // instead.
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task EveryMappedRouteHandlerParameterResolves_ReproducesThe25_06Crash()
    {
        await using var app = BuildRealHost();

        // This call is the entire reproduction. `25-06`'s own postmortem and `ChannelStatusEndpointsTests`'
        // own remarks agree on the mechanism: `UseAuthorization()`'s `AuthorizationPolicyCache` forces
        // `EndpointDataSource.Endpoints` to be built the first time anything in this process needs a
        // named authorization policy, and building that list is what makes Minimal API's
        // `RequestDelegateFactory` infer every mapped delegate's own parameter sources for every
        // mapped endpoint - not only the one route a test happens to call over HTTP. No request is
        // sent below, and none needs to be: `StartAsync` alone is what crashed the demo stand's real
        // process on `25-06`'s incident, before a single visitor's request ever arrived.
        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task MappedRouteCountStaysAboveTheObservedFloor_SoASilentCoverageGapFailsLoudly()
    {
        await using var app = BuildRealHost();
        await app.StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        // Not "some routes exist" - "at least as many routes exist as this suite already knows
        // Program.cs maps". A future CompositionRoot.MapEndpoints that silently stopped calling one
        // MapXxxEndpoints() group (a bad merge, a botched extraction) would still build a
        // non-empty, plausible-looking endpoint list - it just wouldn't be complete, and nothing
        // else in this file would notice, the same "looks green, proves less than it seems to" gap
        // this item exists to close for individual DI registrations. Fault-injected while writing
        // this test (temporarily commented out one MapXxxEndpoints() call inside CompositionRoot,
        // confirmed this specific assertion - and only this one - went red; restored, confirmed
        // green again) - see this item's own worker report for the exact before/after counts.
        Assert.True(
            endpoints.Count >= MinimumExpectedRouteCount,
            $"Expected at least {MinimumExpectedRouteCount} mapped routes (CompositionRoot.MapEndpoints "
                + $"is the one list Program.cs itself maps from); found {endpoints.Count}. A drop this "
                + "large means a route group silently stopped being mapped - fix the gap, don't raise "
                + "this floor to match.");

        await app.StopAsync();
    }

    private static IServiceCollection BuildRealServiceCollection()
    {
        var builder = CreateBuilder();
        CompositionRoot.ConfigureServices(builder);
        return builder.Services;
    }

    private static WebApplication BuildRealHost()
    {
        var builder = CreateBuilder();
        builder.WebHost.UseTestServer();
        CompositionRoot.ConfigureServices(builder);
        RemoveHostedServicesAndStartupOptionsValidation(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        CompositionRoot.MapEndpoints(app);
        return app;
    }

    /// <summary>Found live running this test, in two steps.
    ///
    /// <para><b>Step one:</b> `Microsoft.Extensions.Hosting`'s own generic host resolves
    /// `IEnumerable&lt;IHostedService&gt;` as the first real step of `StartAsync`, before this test's
    /// own target - the lazy endpoint-metadata build `UseAuthorization()` triggers - ever gets a
    /// chance to run, and `CompositionRoot.ConfigureServices` registers every one of `Program.cs`'s
    /// own real background services (`ConnectionHeartbeat`, `NodeDeliveryConsumer`,
    /// `MessagePipelineWorkerHost`, ...) exactly as production does. Starting one of those for real is
    /// exactly the live Redis/RabbitMQ connection this class's own remarks say this test never needs -
    /// and none of them is a Minimal API route handler, so none of them can carry `25-06`'s own bug
    /// class (an unregistered type Minimal API's `RequestDelegateFactory` cannot classify).</para>
    ///
    /// <para><b>Step two, found only after removing the hosted services and re-running:</b> the
    /// generic host's own `StartAsync` separately resolves `IStartupValidator` (singular - one
    /// instance for the whole app, registered `TryAddSingleton` the first time any
    /// `OptionsBuilder&lt;T&gt;.ValidateOnStart()` runs) and calls `.Validate()` on it *before*
    /// starting anything else - a mechanism entirely independent of `IHostedService`, which is why
    /// removing every hosted service above did not also remove this. It fails hard here (ten
    /// simultaneous `OptionsValidationException`s, one per `ValidateOnStart()`-tagged options class
    /// this graph registers) because this test's fake configuration deliberately supplies only the
    /// two-going-on-three keys this class's own remarks name as the synchronous ones - every
    /// `ValidateOnStart` options class Program.cs/ChatModule.cs register is real production
    /// configuration surface with its own already-established test coverage elsewhere in this suite
    /// (each options class's own validator tests), not this item's own concern (`25-06`'s bug was an
    /// unregistered *type*, never an invalid *value*) - so it is removed here for the identical reason
    /// hosted services are.</para>
    ///
    /// <para>Neither removal weakens
    /// <see cref="EveryRegisteredServiceConstructorDependencyResolves_StructuralOnly_NoLiveConnection"/>
    /// above, which already validates every hosted service's own constructor dependencies (and every
    /// `IOptions&lt;T&gt;`'s own generic resolvability) structurally, without starting or validating
    /// either.</para></summary>
    private static void RemoveHostedServicesAndStartupOptionsValidation(IServiceCollection services)
    {
        var descriptorsToRemove = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                || descriptor.ServiceType == typeof(Microsoft.Extensions.Options.IStartupValidator))
            .ToList();
        foreach (var descriptor in descriptorsToRemove)
        {
            services.Remove(descriptor);
        }
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        // A raw env var, not IConfiguration - ChatModule.ConfigureServices' own remarks explain why
        // (AddPostgresPersistence takes a connection string, not an IConfiguration). Same value on
        // every call, so this stays safe even if xUnit runs another test in this class concurrently
        // in the same process - never a real read-modify-write race, just redundant identical writes.
        Environment.SetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING", FakeConnectionString);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(
        [
            new("Auth:Keycloak:Authority", FakeKeycloakAuthority),
            // `AddPlatformObservability` validates PlatformObservabilityOptions synchronously, right
            // inside the registration call (its own remarks: deliberately not deferred to
            // ValidateOnStart) - [Required] on OtelExporterOptions.Endpoint means this key has to be
            // present and URI-shaped, never that it has to be reachable: AddOtlpExporter only stores
            // the endpoint on the exporter's own options; nothing dials it until the SDK's background
            // export processor first flushes a batch, which this test's build-only path never reaches.
            new("Otel:Exporter:Endpoint", "http://192.0.2.1:1"),
        ]);
        return builder;
    }
}
