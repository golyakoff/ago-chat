using System.Security.Cryptography;
using Ago.Chat.Api.Attachments;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Conversations;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Operators;
using Ago.Chat.Api.Owner;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Api.Sites;
using Ago.Chat.Api.Webhooks;
using Ago.Chat.Api.WidgetConfig;
using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.Postgres;
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
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// `7-01`: one call per host, this host's own name - AddPlatformObservability's own remarks on why
// the name is a parameter, not a fourth near-identical appsettings.json.
builder.Services.AddPlatformObservability(builder.Configuration, "Ago.Chat.Api");

// 3-06: readiness now means "can do the job" - Postgres (conversations), RabbitMQ (outbox/fan-out
// consumers), Redis (cache, connection registry) - matching Ago.Chat.Worker's own
// PostgresHealthCheck/RabbitMqHealthCheck pattern (2-04), plus the new Redis check and a drain
// check neither host needed before this slice. Liveness stays the trivial "process responded" check
// both hosts already use - conflating the two is exactly what edge.md warns against.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<DrainHealthCheck>("drain", tags: ["ready"]);
builder.Services.AddSignalR(options =>
{
    // A hub exception's real message and stack trace go to a client only in Development - the
    // generic "Failed to invoke 'X' due to an error on the server" SignalR sends by default is not
    // enough to debug against by hand (dev-harness.html), and never worth risking in production.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

new ChatModule().ConfigureServices(builder.Services, builder.Configuration);

// 5-01: edge.md/api-design.md - CORS is per-site, driven by Site.AllowedOrigins from the database,
// never a wildcard, never an ingress annotation. AddCors() wires the framework's CORS services;
// SiteOriginCorsPolicyProvider replaces the usual named-policy lookup with a per-request decision
// (a preflight cannot say which site it is for, only which Origin - see that class's own remarks and
// CheckCorsOriginHandler's for the two-layer design this is only the first half of).
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, SiteOriginCorsPolicyProvider>();
// 5-01, layer 2: scoped, like the GetSiteConfigByIdHandler it wraps - a hub connection's own DI
// scope (one per connection) is what SignalR gives a Hub's constructor dependencies.
builder.Services.AddScoped<HubOriginValidator>();

// 3-01: Ago.Chat.Api is the only host holding SignalR connections, so it is the only one that
// actually needs the heartbeat running - ChatModule registers the registry's DI surface for every
// host, but only resolving IConnectionRegistry (which this triggers) opens the Redis connection.
builder.Services.AddHostedService<ConnectionHeartbeat>();
builder.Services.AddSingleton<HubConnectionRegistration>();

// 3-02: the receiving half of realtime.md's Fan-out path - consumes this node's own topic and
// pushes to whichever local hub each connection belongs to (SignalRConnectionDispatcher).
builder.Services.AddSingleton<ILocalConnectionDispatcher, SignalRConnectionDispatcher>();
builder.Services.AddHostedService<NodeDeliveryConsumer>();

// 3-04: Ago.Chat.Api is the only host that ever reads the cache (the site-config lookup on the
// widget handshake path), so it is the only one that needs to hear about invalidations - the same
// "registered everywhere, only this host runs the hosted service" shape as NodeDeliveryConsumer above.
builder.Services.AddHostedService<CacheInvalidationConsumer>();

// 3-06: concurrency.md's graceful-shutdown sequence - only Ago.Chat.Api holds hub connections to
// drain, the same "registered everywhere (AddConnectionRegistry), only this host runs the hosted
// service" shape as NodeDeliveryConsumer/ConnectionHeartbeat above.
builder.Services.AddHostedService<ConnectionDrainCoordinator>();

// 4-05: concurrency.md's "In-process pipeline (Api)" - ChatModule registers IMessagePipeline's
// implementation (ChannelMessagePipeline) for every host, the same "registered everywhere, only
// this host runs the hosted service" shape as everything else on this page; only Ago.Chat.Api
// actually drains it, since only Ago.Chat.Api's hubs ever enqueue onto it. ConversationSequencer,
// BatchAccumulator and MessageBatchWriter are internal plumbing MessagePipelineWorkerHost/
// BatchFlusherService share, not needed anywhere else.
builder.Services.AddSingleton<ConversationSequencer>();
builder.Services.AddSingleton<BatchAccumulator>();
builder.Services.AddSingleton<MessageBatchWriter>();
builder.Services.AddHostedService<MessagePipelineWorkerHost>();
builder.Services.AddHostedService<BatchFlusherService>();

// 3-05: bound here, not ChatModule - AuthEndpoints is the only consumer, and it lives in Ago.Chat.Api
// itself (unlike MessageSendRateLimitOptions, which sits beside SendVisitorMessageHandler in
// Application because that handler is registered for every host).
builder.Services
    .AddOptions<VisitorSessionRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(VisitorSessionRateLimitOptions.SectionName))
    .ValidateOnStart();

// `17-08`/`adr/0048`: renewal's own bucket, keyed per visitor rather than per site - a separate
// options type for the same reason it is a separate endpoint (that type's own remarks).
builder.Services
    .AddOptions<VisitorSessionRenewalRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(VisitorSessionRenewalRateLimitOptions.SectionName))
    .ValidateOnStart();

// 3-06: a per-process random key (this project's original Stage 1 choice) only tolerates a single
// Ago.Chat.Api instance - found live, against the 3-replica overlay, when a token issued by one pod
// 401'd on a negotiate request the Gateway's least_conn balancer routed to a different pod (no
// sticky sessions - edge.md - so this is not a rare race, it is the normal case). Auth:SigningKey
// lets every replica share one key (bound from infra-credentials the same way Postgres/RabbitMQ
// passwords already are - docker/.env, gitignored, never committed); its absence falls back to the
// original random-per-process key, which is still correct for the single-instance dotnet-run loop
// local-dev.md describes. Still a throwaway dev value either way - Stage 5's OIDC direction replaces
// this signing story outright, it does not evolve from it (authorization.md).
const string issuer = "ago-chat-api";
var configuredSigningKey = builder.Configuration["Auth:SigningKey"];
var signingKeyBytes = configuredSigningKey is { Length: > 0 }
    ? Convert.FromBase64String(configuredSigningKey)
    : RandomNumberGenerator.GetBytes(32);
var signingKey = new SymmetricSecurityKey(signingKeyBytes);
var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
builder.Services.AddSingleton(sp => new JwtTokenService(signingCredentials, issuer, sp.GetRequiredService<IClock>()));

// `5-05`/`adr/0022`: the Operator scheme's issuer/signing key now comes from Keycloak, not the
// local SigningCredentials below - Authority (required, fails fast like AGO_CHAT_CONNECTION_STRING)
// drives ASP.NET Core's own JWKS discovery, so there is no local key to configure for this scheme at
// all. RequireHttpsMetadata off by default: no host in this project terminates TLS internally
// (edge.md - that is the Gateway's job), and local Keycloak runs over plain HTTP.
var keycloakAuthority = builder.Configuration["Auth:Keycloak:Authority"]
    ?? throw new InvalidOperationException(
        "Set Auth:Keycloak:Authority - e.g. http://localhost:8081/realms/ago-chat for the local compose loop.");
var keycloakAudience = builder.Configuration["Auth:Keycloak:Audience"] ?? "ago-console";
var keycloakRequireHttpsMetadata = builder.Configuration.GetValue("Auth:Keycloak:RequireHttpsMetadata", false);

builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

builder.Services.AddAuthentication()
    .AddJwtBearer(JwtSchemes.Visitor, options =>
    {
        // Without this, ASP.NET Core silently remaps short JWT claim names ("sub") to long
        // legacy ClaimTypes URIs during validation, so reading the same "sub" name back
        // (ClaimsPrincipalExtensions) finds nothing - found by running this against a real
        // token and seeing FindFirstValue return null even though the JWT payload clearly had it.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = TokenValidationParametersFor(JwtSchemes.Visitor);
        options.Events = HubTokenFromQueryString("/hubs/visitor");
    })
    .AddJwtBearer(JwtSchemes.Operator, options =>
    {
        options.MapInboundClaims = false;
        options.Authority = keycloakAuthority;
        options.RequireHttpsMetadata = keycloakRequireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = keycloakAudience,
            ValidateLifetime = true,
            // ValidateIssuer/ValidateIssuerSigningKey stay at their default (true) - Authority above
            // is what supplies the expected issuer and the JWKS to validate the signature against,
            // discovered automatically rather than configured by hand the way the Visitor scheme's
            // local key is.
        };
        options.Events = HubTokenFromQueryString("/hubs/operator");
    });
builder.Services.AddAuthorization(options =>
{
    // `5-05`: turns "no operator matched this Keycloak subject" into a clean rejection at the
    // authorization layer - OperatorIdentityClaimsTransformation adds no OperatorId claim when the
    // lookup fails, and without this policy that would only surface as
    // ClaimsPrincipalExtensions.GetOperatorId throwing deep inside a handler instead.
    options.AddPolicy("RequireOperatorIdentity", policy => policy
        .AddAuthenticationSchemes(JwtSchemes.Operator)
        .RequireClaim(AgoClaimTypes.OperatorId));

    // `10-01`/`adr/0028`: strictly weaker than RequireOperatorIdentity above - same scheme, same
    // Keycloak JWKS validation, but no RequireClaim(OperatorId). Accepts any token that is
    // signature/audience/lifetime-valid against Keycloak, including one whose `sub` resolves to no
    // `operators` row at all - the exact state a freshly self-registered visitor is in before `10-02`'s
    // bootstrap endpoint ever runs. Gates *only* that one endpoint (POST /api/v1/sites) - never wired
    // onto any other route, because a token accepted here proves nothing about site membership or
    // adr/0016 permissions, only "a real person completed Keycloak's login/registration flow." See
    // adr/0028 for why this must stay a second, narrower policy rather than relaxing
    // RequireOperatorIdentity itself.
    options.AddPolicy("RequireKeycloakIdentity", policy => policy
        .AddAuthenticationSchemes(JwtSchemes.Operator)
        .RequireAuthenticatedUser());

    // `12-01`/`adr/0032`: the platform owner - not an operator with a lot of permissions, a
    // structurally different caller. Same scheme and the same Keycloak JWKS validation as the two
    // policies above (adr/0028's "which claims are required afterward is exactly what the policy
    // layer exists to express"), but the claim it requires is one Keycloak signs and this codebase
    // can never write: a `platform-owner` entry in `realm_access.roles`. No OperatorId, no SiteId, no
    // IPermissionChecker - so no grant in the `roles`/`operator_roles` tables, however broadly
    // seeded, can satisfy it. RequireAuthenticatedUser is strictly redundant next to the requirement
    // below (an anonymous principal carries no claims, and the handler checks IsAuthenticated
    // itself) - kept as an explicit statement of intent, matching RequireKeycloakIdentity's own
    // shape, not as load-bearing logic.
    options.AddPolicy("RequirePlatformOwner", policy => policy
        .AddAuthenticationSchemes(JwtSchemes.Operator)
        .RequireAuthenticatedUser()
        .AddRequirements(new PlatformOwnerRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

var app = builder.Build();

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

app.MapAuthEndpoints();
app.MapAttachmentEndpoints();
app.MapConversationsEndpoints();
app.MapOperatorsEndpoints();
app.MapWebhookEndpoints();
app.MapWidgetConfigEndpoints();
app.MapSitesEndpoints();
// `12-02`: the platform owner's cross-tenant read - the only route here not scoped to one site,
// and the only one carrying `12-01`'s RequirePlatformOwner policy (OwnerSitesEndpoints' remarks).
app.MapOwnerEndpoints();
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

TokenValidationParameters TokenValidationParametersFor(string audience) => new()
{
    ValidateIssuer = true,
    ValidIssuer = issuer,
    ValidateAudience = true,
    ValidAudience = audience,
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = signingKey,
    ValidateLifetime = true,
};

// SignalR's WebSocket upgrade cannot carry an Authorization header, so the client passes the token
// as ?access_token=... instead - restricted to this hub's own path, never accepted on ordinary
// HTTP requests (the standard ASP.NET Core SignalR JWT pattern).
JwtBearerEvents HubTokenFromQueryString(string hubPath) => new()
{
    OnMessageReceived = context =>
    {
        var accessToken = context.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments(hubPath))
        {
            context.Token = accessToken;
        }

        return Task.CompletedTask;
    },
};
