using Ago.Chat.Api;
using Ago.Chat.Api.Attachments;
using Ago.Chat.Api.CannedResponses;
using Ago.Chat.Api.ModuleTaskChannelPreferences;
using Ago.Chat.Api.ChannelIdentities;
using Ago.Chat.Api.Consent;
using Ago.Chat.Api.ContactDetails;
using Ago.Chat.Api.PhoneVerification;
using Ago.Chat.Api.Notes;
using Ago.Chat.Api.Tags;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Billing;
using Ago.Chat.Api.Channels;
using Ago.Chat.Api.Conversations;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Demo;
using Ago.Chat.Api.Documents;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.TenantScopeDiagnostics;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Microsoft.Extensions.Options;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Me;
using Ago.Chat.Api.Operators;
using Ago.Chat.Api.OperatorInvites;
using Ago.Chat.Api.Owner;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Api.ReplyDraft;
using Ago.Chat.Api.Sites;
using Ago.Chat.Api.Webhooks;
using Ago.Chat.Api.Modules;
using Ago.Chat.Api.AssignmentPenalty;
using Ago.Chat.Api.ContactVisibility;
using Ago.Chat.Api.OfflineAutoReply;
using Ago.Chat.Api.WidgetConfig;
using Ago.Chat.Api.WidgetActivity;
using Ago.Chat.Api.Storage;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module;
using Ago.Chat.Module.Pipeline;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Observability;
using Ago.Platform.Kernel;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// `25-07`: the real production DI-registration sequence, extracted to CompositionRoot.ConfigureServices
// so Ago.Chat.Integration.Tests' RouteHandlerDiRegistrationTests can call the exact same code a
// top-level-statement Program.cs could never expose to a caller otherwise - see that class's own
// remarks for why.
CompositionRoot.ConfigureServices(builder);
var app = builder.Build();

// Before everything else in the pipeline, deliberately: `UseCors`/`UseAuthentication` and every
// handler after them (`RegisterSiteHandler`, `MintDemoTenantHandler`) reads
// `HttpContext.Connection.RemoteIpAddress` assuming it is already the real client - this middleware
// is what makes that assumption true, by rewriting it from `X-Forwarded-For` before anything else
// runs.
app.UseForwardedHeaders();

// `8-08`/`adr/0056`: run before anything can listen, and deliberately not as an IHostedService -
// GenericWebHostService opens the socket before any service registered after it, so a hosted service
// that threw would do so with requests already arriving. A host whose database is behind the
// migrations its own build carries refuses to start rather than serving 200s for pages whose queries
// fail; that is the 2026-08-25 incident, closed. It is also the whole of this system's deploy
// ordering: nothing orchestrates "migrator Job first", the hosts simply do not come up until it has
// run. See SchemaVersionGuard for why this beats an init container and where the expected version
// comes from.
await app.Services.EnsureSchemaIsCurrentAsync();

// `17-03`: resolving the ring once here runs its whole validation - base64, key length, "exactly one
// key with no RetiredAt", a drain window at least as long as the token lifetime. Deliberately eager:
// a singleton is otherwise constructed on the first request that needs it, so a botched rotation
// would first appear as a 500 on one visitor's request rather than as a host that would not start.
// Same reasoning as EnsureSchemaIsCurrentAsync above, one line earlier in the same window.
_ = app.Services.GetRequiredService<IVisitorSigningKeyRing>();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// 3-06: readiness and liveness genuinely diverge now (edge.md) - readiness runs the "ready"-tagged
// checks above (dependencies plus drain state), liveness stays the trivial "process responded"
// check Ago.Chat.Worker already uses (Predicate: _ => false runs no registered check at all).
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// `15-06`: which commit this process was built from, readable without cluster access. The image tag
// (a commit SHA since 15-06) says which artifact was asked for; this says what is actually running,
// because it comes from the compiled binary rather than from a manifest anyone can edit. Sits next
// to the health checks and outside authentication on purpose - the commit of a public repository is
// not a secret, and a version check that needs a token is a version check nobody runs.
app.MapGet("/healthz/version", () => BuildInfoResponse.For(typeof(Program).Assembly));

// `7-02` fix: AddPlatformObservability wires the Prometheus exporter into the MeterProvider, but
// mapping the actual scrape endpoint needs the built app (endpoint routing), not just the service
// collection - so this one line lives per host, same as the health-check maps above.
app.MapPrometheusScrapingEndpoint();

// `25-07`: the real production route-mapping sequence, extracted to CompositionRoot.MapEndpoints for
// the identical reason as ConfigureServices above.
CompositionRoot.MapEndpoints(app);
app.MapHub<VisitorHub>("/hubs/visitor");
app.MapHub<OperatorHub>("/hubs/operator");

if (app.Environment.IsDevelopment())
{
    // The manual two-tab verification harness (1-06) - same-origin, so it never exercises the CORS
    // policy above at all. Real cross-origin widget CORS shipped in 5-01 (api-design.md).
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.Run();
