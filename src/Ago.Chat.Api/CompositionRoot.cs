using Ago.Chat.Api.Attachments;
using Ago.Chat.Api.Branding;
using Ago.Chat.Api.CannedResponses;
using Ago.Chat.Api.ModuleTaskChannelPreferences;
using Ago.Chat.Api.ChannelIdentities;
using Ago.Chat.Api.Consent;
using Ago.Chat.Api.ContactDetails;
using Ago.Chat.Api.PhoneVerification;
using Ago.Chat.Api.Notes;
using Ago.Chat.Api.Persons;
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
using Ago.Chat.Api.AiAddOn;
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

namespace Ago.Chat.Api;

/// <summary>
/// `25-07`: the exact service-registration and route-mapping sequence <c>Program.cs</c> runs, moved
/// here as two reusable methods instead of inline top-level statements, for one reason only - a
/// top-level-statement <c>Program.cs</c> cannot be called into from a test at all, so the only way a
/// test can compose "the real thing" rather than a hand-rebuilt stand-in of it (`OperatorInviteEndpointTests`'/
/// `ChannelStatusEndpointsTests`' own gap - see each class's own remarks) is for "the real thing" to be
/// a method both `Program.cs` and a test can call. This is a pure extract-method refactor - every
/// statement below is byte-for-byte what previously lived inline in `Program.cs`, in the same order,
/// with no logic changed; `Program.cs` itself now reads as the two calls below plus the handful of
/// steps that only make sense against a real host (health checks, `EnsureSchemaIsCurrentAsync`, the
/// eager `IVisitorSigningKeyRing` resolution, the forwarded-headers/CORS/auth middleware activation,
/// SignalR's own `MapHub` calls) - see `RouteHandlerDiRegistrationTests`' own remarks in
/// `Ago.Chat.Integration.Tests` for what this seam makes possible.
///
/// <para><b>Why `MapHub&lt;VisitorHub&gt;`/`MapHub&lt;OperatorHub&gt;` stay in `Program.cs`, not here.</b>
/// SignalR hub methods are not Minimal API endpoints - they are invoked through
/// <c>IHubProtocol</c>'s own method-dispatch, which resolves a hub instance's constructor dependencies
/// per connection through the ordinary DI container, not through `RequestDelegateFactory`'s parameter-
/// source inference. `25-06`'s bug class - an unregistered type Minimal API cannot classify as Route,
/// Query, Body or Service - has no SignalR equivalent, so extracting the two `MapHub` calls into this
/// file's own `MapEndpoints` would test a mechanism this item was never about.</para>
///
/// <para><b>Why `AddHealthChecks`/`AddSignalR`/`MapHealthChecks`/`MapPrometheusScrapingEndpoint`/
/// `MapGet("/healthz/version")` also stay in `Program.cs`.</b> None of these map a user-authored Minimal
/// API delegate with parameters Minimal API has to infer a source for - they are framework-built
/// endpoints (`HealthChecksEndpointRouteBuilderExtensions`, OpenTelemetry's own Prometheus exporter, a
/// parameterless closure) with nothing for `RequestDelegateFactory` to get wrong the way it got
/// `PreviewOperatorInviteHandler` wrong. Leaving them in `Program.cs` keeps this file's own surface
/// limited to exactly the two things `25-07`'s test needs to be real: the service graph, and the set of
/// product routes whose handlers Minimal API infers parameter sources for.</para>
/// </summary>
public static class CompositionRoot
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddPlatformObservability(builder.Configuration, "Ago.Chat.Api");

        // `24-17`: registered here rather than in Ago.Chat.Module - the same "explicit when actually used"
        // reasoning Ago.Chat.Api.csproj's own remarks give for referencing Keycloak/MaxBot/etc. directly.
        // Only this host's owner-only endpoint (OwnerTenantIsolationEndpoints) ever resolves
        // ITenantScopeInspector; Ago.Chat.Worker and Ago.Chat.Webhooks have no route that would, and Module
        // composes services every host shares.
        builder.Services.AddTenantScopeDiagnostics();

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

        // `8-07`/`adr/0058`: the demo-credential minting path, wired here rather than in ChatModule -
        // deliberately, and the reason is the credential. ChatModule runs in every host, so registering the
        // Keycloak admin client there would make its client secret a required setting for Ago.Chat.Webhooks
        // too, which has no business holding one. Ago.Chat.Api mints and Ago.Chat.Worker expires; nothing else
        // is handed the secret at all.
        builder.Services
            .AddOptions<DemoTenantOptions>()
            .Bind(builder.Configuration.GetSection(DemoTenantOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // `23-45`: a cross-property rule that no data annotation can express - if minting is on, the shared
        // published account must be named. See DemoTenantOptionsValidator's own remarks: an empty list there
        // silently removes the console's standing warning from the one account whose password is on a public
        // page, so this refuses to start rather than serving a console that is quietly less honest.
        builder.Services.AddSingleton<IValidateOptions<DemoTenantOptions>, DemoTenantOptionsValidator>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DemoTenantOptions>>().Value);
        builder.Services
            .AddOptions<DemoTenantRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(DemoTenantRateLimitOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DemoTenantRateLimitOptions>>().Value);
        builder.Services.AddKeycloakDemoIdentities(builder.Configuration);
        builder.Services.AddScoped<MintDemoTenantHandler>();

        // `25-73`: the identical "wired here, not in ChatModule" reasoning right above, restated for the
        // second Keycloak-writing handler this codebase has - CreateOperatorInviteHandler now depends on
        // IOperatorInviteEmailProvisioner, which carries the same service-account credential.
        // Ago.Chat.Worker/Ago.Chat.Webhooks never send an invite, so they never register it and never hold it.
        builder.Services.AddKeycloakOperatorInviteEmails(builder.Configuration);
        builder.Services.AddScoped<CreateOperatorInviteHandler>();

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
        // `5-18`: the operator hub's own origin check. Bound and validated at startup - an unset list means
        // no operator can connect, so a host without one refuses to boot rather than refusing every operator
        // silently (ConsoleOriginOptions' own remarks).
        builder.Services
            .AddOptions<ConsoleOriginOptions>()
            .Bind(builder.Configuration.GetSection(ConsoleOriginOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ConsoleOriginOptions>>().Value);
        builder.Services.AddSingleton<ConsoleOriginValidator>();

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
        // the hosts that actually enqueue drain it" shape as everything else on this page. Ago.Chat.Api's
        // hubs are the original enqueuer; `14-02` gave Ago.Chat.Worker's own MaxLongPollingService a second
        // one (its own Program.cs registers the identical five lines below, for the identical reason - see
        // that host's own remarks on the bug this was found fixing). ConversationSequencer, BatchAccumulator
        // and MessageBatchWriter are internal plumbing MessagePipelineWorkerHost/BatchFlusherService share,
        // not needed anywhere else.
        builder.Services.AddSingleton<ConversationSequencer>();
        builder.Services.AddSingleton<BatchAccumulator>();
        builder.Services.AddSingleton<MessageBatchWriter>();
        builder.Services.AddHostedService<MessagePipelineWorkerHost>();
        builder.Services.AddHostedService<BatchFlusherService>();

        // `23-07`: the funnel's own accumulate-and-flush pair, registered here rather than in ChatModule for
        // the identical "internal pipeline plumbing, resolved by exactly the host that runs it" reason as
        // ConversationSequencer/BatchAccumulator/MessageBatchWriter directly above - only Ago.Chat.Api's
        // AuthEndpoints/WidgetActivityEndpoints/VisitorHub ever call IWidgetActivityRecorder at all.
        builder.Services
            .AddOptions<WidgetActivityOptions>()
            .Bind(builder.Configuration.GetSection(WidgetActivityOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<WidgetActivityAccumulator>();
        builder.Services.AddSingleton<IWidgetActivityRecorder>(sp => sp.GetRequiredService<WidgetActivityAccumulator>());
        builder.Services.AddSingleton<WidgetActivityWriter>();
        builder.Services.AddHostedService<WidgetActivityFlusherService>();

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

        // `23-07`: the beacon's own bucket, per IP rather than per site or per visitor - see
        // WidgetActivityBeaconRateLimitOptions' own remarks for why.
        builder.Services
            .AddOptions<WidgetActivityBeaconRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(WidgetActivityBeaconRateLimitOptions.SectionName))
            .ValidateOnStart();

        // `24-02`: the published surface's own per-IP bucket - bound here, not ChatModule, the same
        // "DocumentEndpoints is the only consumer and lives in Ago.Chat.Api itself" reasoning
        // VisitorSessionRateLimitOptions's own remarks give right above.
        builder.Services
            .AddOptions<PublishedDocumentReadRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(PublishedDocumentReadRateLimitOptions.SectionName))
            .ValidateOnStart();

        // `23-70`: the invite landing page's own per-IP bucket - bound here, not ChatModule, the identical
        // "the endpoint lives in Ago.Chat.Api itself" reasoning the two options right above already give.
        builder.Services
            .AddOptions<OperatorInvitePreviewRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(OperatorInvitePreviewRateLimitOptions.SectionName))
            .ValidateOnStart();

        // 3-06: a per-process random key (this project's original Stage 1 choice) only tolerates a single
        // Ago.Chat.Api instance - found live, against the 3-replica overlay, when a token issued by one pod
        // 401'd on a negotiate request the Gateway's least_conn balancer routed to a different pod (no
        // sticky sessions - edge.md - so this is not a rare race, it is the normal case). Auth:SigningKey
        // lets every replica share one key (bound from infra-credentials the same way Postgres/RabbitMQ
        // passwords already are - docker/.env, gitignored, never committed); its absence falls back to the
        // original random-per-process key, which is still correct for the single-instance dotnet-run loop
        // local-dev.md describes.
        //
        // `17-03`/`adr/0067`: all three of those forms still work, and there is now a fourth that is the
        // point of the item - `Auth:VisitorSigningKeys`, a *set*. One key issues; several validate; a
        // retired key drops out of the validation set on its own once its drain window closes. Before this,
        // the only key that validated was the only key that signed, so rotating it logged out every visitor
        // on every site at the same instant, which is why it had never been rotated. See
        // VisitorSigningKeyRing.FromConfiguration for the precedence between the forms and for why having
        // both of the first two set is a refusal to start rather than a precedence rule.
        const string issuer = "ago-chat-api";
        builder.Services.AddSingleton<IVisitorSigningKeyRing>(sp =>
            VisitorSigningKeyRing.FromConfiguration(builder.Configuration, sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton(sp => new JwtTokenService(
            sp.GetRequiredService<IVisitorSigningKeyRing>(), issuer, sp.GetRequiredService<IClock>()));

        // `5-05`/`adr/0022`: the Operator scheme's issuer/signing key now comes from Keycloak, not the
        // visitor key ring above - Authority (required, fails fast like AGO_CHAT_CONNECTION_STRING)
        // drives ASP.NET Core's own JWKS discovery, so there is no local key to configure for this scheme at
        // all. RequireHttpsMetadata off by default: no host in this project terminates TLS internally
        // (edge.md - that is the Gateway's job), and local Keycloak runs over plain HTTP.
        var keycloakAuthority = builder.Configuration["Auth:Keycloak:Authority"]
            ?? throw new InvalidOperationException(
                "Set Auth:Keycloak:Authority - e.g. http://localhost:8081/realms/ago-chat for the local compose loop.");
        var keycloakAudience = builder.Configuration["Auth:Keycloak:Audience"] ?? "ago-console";
        var keycloakRequireHttpsMetadata = builder.Configuration.GetValue("Auth:Keycloak:RequireHttpsMetadata", false);

        // `13-07`/`adr/0068`: OperatorIdentityClaimsTransformation needs the current request to read the
        // active-site signal off (a header for an ordinary REST call, a query-string parameter for the
        // SignalR hub handshake - that class's own remarks explain why both). IClaimsTransformation has no
        // HttpContext parameter of its own; this is the framework's own seam for reaching the ambient request
        // from a singleton service, registered here rather than left implicit because nothing in this codebase
        // needed it before this item.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Visitor, options =>
            {
                // Without this, ASP.NET Core silently remaps short JWT claim names ("sub") to long
                // legacy ClaimTypes URIs during validation, so reading the same "sub" name back
                // (ClaimsPrincipalExtensions) finds nothing - found by running this against a real
                // token and seeing FindFirstValue return null even though the JWT payload clearly had it.
                options.MapInboundClaims = false;
                options.Events = HubTokenFromQueryString("/hubs/visitor");
                // TokenValidationParameters is configured separately below - it needs the key ring, and this
                // overload has no service provider to resolve one from.
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

        // `17-03`/`adr/0067`: the Visitor scheme's validation parameters, configured with the service
        // provider so they can close over the key ring. The single line that makes rotation work is
        // IssuerSigningKeyResolver: a delegate the handler calls on *every* token, where the previous
        // IssuerSigningKey was one key captured while the host was starting. That is what lets a retired key
        // leave the accepted set the moment its drain window closes, with no restart and no deploy.
        builder.Services
            .AddOptions<JwtBearerOptions>(JwtSchemes.Visitor)
            .Configure<IVisitorSigningKeyRing>((options, signingKeys) =>
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = JwtSchemes.Visitor,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => signingKeys.ValidationKeys(),
                    ValidateLifetime = true,
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

        // `edge.md`'s own stated-but-never-enforced requirement: "the app must be configured to trust
        // [X-Forwarded-For], or every per-IP limit silently applies to the ingress itself." Found live, not
        // in review - every `demo-mint:ip:*`/`register-site:ip:*` Redis key was the Gateway pod's own
        // cluster-internal address (confirmed against `kubectl get pods -o wide`), meaning the "per-IP" rate
        // limiter was one shared bucket for every visitor on the internet, not one bucket per visitor. One
        // person's testing could - and did - lock every other visitor out of minting a demo tenant.
        //
        // `KnownNetworks` trusts the k3s pod network (`10.42.0.0/16`, this cluster's own CIDR, confirmed
        // against the Gateway's and every other pod's actual IP) rather than the default (loopback only,
        // which nothing here ever connects from) or leaving it wide open (which would let any caller forge
        // `X-Forwarded-For` and pick their own rate-limit bucket - the header is otherwise fully
        // caller-controlled). `KnownProxies` stays empty: this deployment's one hop is the Gateway, entirely
        // inside the trusted network already covered by `KnownNetworks`.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.42.0.0/16"));
        });
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapAuthEndpoints();
        app.MapWidgetActivityEndpoints();
        app.MapAttachmentEndpoints();
        // `23-80`/`23-82`: own Map call - "Администрирование -> Хранилище"'s own read/bulk-delete surface,
        // entirely separate from the per-attachment create/confirm/download/delete routes above it.
        app.MapSiteAttachmentStorageEndpoints();
        app.MapConversationsEndpoints();
        // `23-69`/`23-77`: own Map call, the identical "a test host mapping only MapConversationsEndpoints
        // must never be made to resolve GetVisitorRestrictionsForSiteHandler/LiftVisitorRestrictionHandler
        // just because this route happened to share a folder" reasoning `MapAccessRecordsEndpoint` already
        // states for itself further down this file.
        app.MapVisitorRestrictionsEndpoints();
        // `25-143`: own Map call, the identical "a test host mapping only MapConversationsEndpoints
        // must never be made to resolve a route with a different auth shape just because it shares a
        // folder" reasoning MapVisitorRestrictionsEndpoints' own remarks give right above - here in the
        // opposite direction (visitor-only beside an operator-only file).
        app.MapUnreadCountEndpoints();
        app.MapOperatorsEndpoints();
        // `13-07`/`adr/0068`
        app.MapMeEndpoints();
        // `26-03`/`adr/0179`: push device registration - the caller's own row, RequireOperatorIdentity
        // throughout, no site-wide permission to check (MeDeviceEndpoints' own remarks).
        app.MapMeDeviceEndpoints();
        app.MapWebhookEndpoints();
        // `14-02`: the inbound receiver (MAX's own production mechanism) and the console's own connect/
        // disconnect flow - see MaxWebhookEndpoints' own remarks for why this host, not Ago.Chat.Webhooks.
        app.MapMaxWebhookEndpoints();
        app.MapMaxChannelEndpoints();
        // `14-07`: the console's own Telegram connect/disconnect flow. No MapTelegramWebhookEndpoints - this
        // channel has no webhook receiver at all (TelegramBotApiOptions' own remarks); its inbound mechanism,
        // TelegramLongPollingService, is registered on Ago.Chat.Worker instead, the same "restart-tolerant
        // background work with no request to answer" reasoning MaxLongPollingService's own registration uses.
        app.MapTelegramChannelEndpoints();
        // `14-08`: the inbound receiver (VK's own and only production mechanism) and the console's own
        // connect/disconnect flow - see VkWebhookEndpoints' own remarks for why this host, not
        // Ago.Chat.Webhooks, matching MaxWebhookEndpoints' own precedent.
        app.MapVkWebhookEndpoints();
        app.MapVkChannelEndpoints();
        // `14-11`: the inbound receiver and the console's own connect/disconnect flow, the identical
        // "this host, not Ago.Chat.Webhooks" reasoning MaxWebhookEndpoints'/VkWebhookEndpoints' own remarks
        // give - Avito's own delivery, like MAX's/VK's, is request-shaped, not third-party-latency-shaped.
        app.MapAvitoWebhookEndpoints();
        app.MapAvitoChannelEndpoints();
        // `14-10`: the inbound receiver (WhatsApp's own and only production mechanism - a single, App-wide
        // route, not one per credential; WhatsAppWebhookEndpoints' own remarks explain why) and the console's
        // own connect/disconnect flow - matching MaxWebhookEndpoints'/VkWebhookEndpoints' own precedent for
        // living in this host rather than Ago.Chat.Webhooks.
        app.MapWhatsAppWebhookEndpoints();
        app.MapWhatsAppChannelEndpoints();
        // `14-09`: the inbound receiver, and the only mapping this channel needs - EmailBotApiOptions' own
        // remarks explain why there is no MapEmailChannelEndpoints: this channel has no per-tenant secret for a
        // console connect/disconnect flow to manage at all, unlike every channel above.
        app.MapEmailWebhookEndpoints();
        app.MapWidgetConfigEndpoints();
        // `25-160`: the tenant's own reply-email brand (company name, logo upload) - a settings
        // resource, not a connect/disconnect flow (this channel still has neither, unchanged from
        // `MapEmailWebhookEndpoints`'s own remarks a few lines up), so it gets its own small endpoint
        // group rather than a `MapEmailChannelEndpoints` this channel still has no other reason to grow.
        app.MapSiteBrandingEndpoints();
        // `24-02`: the published surface's own unauthenticated read routes - see DocumentEndpoints' own
        // remarks for why they are mapped without any RequireAuthorization policy at all.
        app.MapDocumentEndpoints();
        // `19-03`/`23-83`: a tenant's own read of which modules are enabled on their site. The write half
        // `19-03` built here (`ModuleEndpoints`'s own remarks) is gone - provisioning is the platform's act,
        // not a tenant's (`adr/0151`).
        app.MapModuleEndpoints();
        // `14-04`
        app.MapOfflineAutoReplyEndpoints();

        // `25-04`: the tenant's own AI add-on surface - accept, declare, enable, disable, read.
        app.MapAiAddOnEndpoints();
        app.MapAssignmentPenaltyEndpoints();
        // `23-11`: the account-wide contact-visibility rung's own settings-screen read/write.
        app.MapContactVisibilityEndpoints();
        // `18-03`
        app.MapCannedResponseEndpoints();
        // `18-04`
        app.MapNoteEndpoints();
        // `adr/0184`: the account's person registry, read for display by the consoles - and the
        // operator's notes about a person (O3).
        app.MapPersonEndpoints();
        app.MapTagEndpoints();
        // `14-12`: verified channel-identity linking/unlinking - the console-initiated link request, the
        // VisitorPanel listing, and the operator-gated unlink. The platform owner's own unconditional unlink is
        // the separate MapOwnerChannelIdentityEndpoints call below, next to MapOwnerEndpoints.
        app.MapChannelIdentityEndpoints();
        // `20-11`: the per-booking priority list - a narrower, per-booking override sitting in front of
        // `14-13`'s own preference above. See ModuleTaskChannelPreferenceEndpoints' own remarks on why no console
        // page calls this yet.
        app.MapModuleTaskChannelPreferenceEndpoints();
        // `14-14`/`adr/0079` section 6: unverified contact details - a separate, simpler surface beside
        // channel identities, never reaching IChannelIdentityRepository or DeliverChannelMessageHandler.
        app.MapContactDetailEndpoints();
        // `24-05`: the visitor's own consent read/accept pair, and the tenant's own publish route for their
        // site-scoped consent document - see each file's own remarks for why neither reaches
        // OwnerDocumentEndpoints'/DocumentEndpoints' shared RequirePlatformOwner-vs-anonymous split.
        app.MapConsentEndpoints();
        app.MapSiteConsentDocumentEndpoints();
        // `14-15`/`adr/0079`: phone verification via a proactive SMS/voice code - visitor-only (see
        // PhoneVerificationEndpoints' own remarks), the third caller of the dual-scheme EitherTokenKind policy
        // after AttachmentEndpoints.
        app.MapPhoneVerificationEndpoints();
        app.MapSitesEndpoints();
        // This item's own console screen: own Map call, not folded into MapSitesEndpoints above - see
        // SitesEndpoints' MapExportHistoryEndpoint's own remarks for why (a test host that maps only
        // MapSitesEndpoints must never be made to resolve GetSiteExportHistoryHandler just because this route
        // happened to share its file).
        app.MapExportHistoryEndpoint();
        // `24-12`: own Map call, not folded into MapSitesEndpoints above - see SitesEndpoints'
        // MapAccessRecordsEndpoint's own remarks for why (a test host that maps only MapSitesEndpoints must
        // never be made to resolve GetAccessRecordsForSiteHandler just because this route happened to share
        // its file).
        app.MapAccessRecordsEndpoint();
        // `23-11`: own Map call, the identical reason the line above is - see MapContactRevealsEndpoint's
        // own remarks.
        app.MapContactRevealsEndpoint();
        // `23-52`: own Map call, the identical reason the two lines above are - see
        // MapTenantAgreementsEndpoint's own remarks (a test host that maps only MapSitesEndpoints must never
        // be made to resolve GetTenantAgreementsForSiteHandler just because this route happened to share its
        // file).
        app.MapTenantAgreementsEndpoint();
        // `10-06`: own file, own Map call - see SiteInstallationEndpoints' own remarks on why it is not
        // folded into MapSitesEndpoints above.
        app.MapSiteInstallationEndpoints();
        // `25-70`: own file, own Map call - see SiteSuspensionEndpoints' own remarks; the tenant's own read of
        // its own account's suspension state.
        app.MapSiteSuspensionEndpoints();
        // `25-83`: own file, own Map call - see DownloadUsageEndpoints' own remarks; the tenant's own read of
        // its own account's download usage, driving the console's own warning banner.
        app.MapDownloadUsageEndpoints();
        // `13-01`
        app.MapOperatorInviteEndpoints();
        // `8-07`: the anonymous demo-credential route. Registered unconditionally; the handler refuses when
        // DemoTenant:Enabled is false, so a deployment that has not opted in answers a clear
        // "not enabled here" rather than a 404 that reads like a bug (MintDemoTenantHandler's own remarks).
        app.MapDemoEndpoints();
        // `12-02`: the platform owner's cross-tenant read - the only route here not scoped to one site,
        // and the only one carrying `12-01`'s RequirePlatformOwner policy (OwnerSitesEndpoints' remarks).
        app.MapOwnerEndpoints();
        // `23-14`: the per-tenant detail companion - its own Map call, not folded into the one above
        // (OwnerSitesEndpoints' own class remarks say why).
        app.MapOwnerSiteDetailEndpoint();
        // `14-12`: the platform owner's first write/action surface - see OwnerChannelIdentityEndpoints' own
        // remarks for why this is a separate route from ChannelIdentityEndpoints' operator-gated unlink above,
        // even though both ultimately call Domain.ChannelIdentity.Unlink.
        app.MapOwnerChannelIdentityEndpoints();
        // `22-17`: the platform owner's own module grant/revoke - a deliberate cross-tenant write, gated by
        // RequirePlatformOwner exactly as the two owner surfaces above are (OwnerModuleEndpoints' own remarks).
        app.MapOwnerModuleEndpoints();
        // `23-85`: the platform owner's own review-then-disconnect walkthrough for channel credentials
        // connected without an entitlement - deliberately its own route, reached only by a deliberate,
        // authenticated owner action, never by anything this deploy runs unattended
        // (OwnerChannelEntitlementEndpoints' own remarks).
        app.MapOwnerChannelEntitlementEndpoints();
        // `23-68`: the platform owner's own recovery write - restoring a locked-out operator's seat, gated by
        // RequirePlatformOwner exactly as every owner surface above is (OwnerOperatorsEndpoints' own remarks).
        app.MapOwnerOperatorsEndpoints();
        app.MapOwnerSeatGrantsEndpoints();
        // `25-76`: the platform owner's own role-permission tool - adding a permission a tenant's role is
        // missing, gated by RequirePlatformOwner exactly as every owner surface above is
        // (OwnerRolesEndpoints' own remarks).
        app.MapOwnerRolesEndpoints();
        // `23-48`: the platform owner's own write for a tenant's allowed origins - its own Map call, the same
        // "own file, own registration" discipline OwnerSiteAllowedOriginsEndpoints' own remarks describe.
        app.MapOwnerSiteAllowedOriginsEndpoint();
        // `22-08`/`adr/0166`: the platform owner's own account-wide freeze - suspend/extend/unblock and the
        // console's own currently-suspended list, gated by RequirePlatformOwner exactly as every owner surface
        // above is (OwnerSuspensionEndpoints' own remarks).
        app.MapOwnerSuspensionEndpoints();
        // `25-83`: the platform owner's own per-tenant download-block exemption - own file, own Map call,
        // the identical discipline OwnerSuspensionEndpoints' own remarks state just above.
        app.MapOwnerDownloadBlockExemptionEndpoints();
        // `25-84`
        app.MapOwnerDownloadOverageBillingModeEndpoints();
        // `24-02`: the named owner's own publish route - see OwnerDocumentEndpoints' own remarks for why
        // RequirePlatformOwner is the entire access-control story here too.
        app.MapOwnerDocumentEndpoints();
        // `25-20`: the platform owner's own price-list read - its own Map call, the same "own file, own
        // registration" discipline every owner surface above already follows (OwnerPricingEndpoints' own
        // remarks).
        app.MapOwnerPricingEndpoint();
        // `24-17`: the platform owner's own read of this deployment's live tenant-isolation figures - its own
        // Map call, the same "own file, own registration" discipline every owner surface above already
        // follows (OwnerTenantIsolationEndpoints' own remarks).
        app.MapOwnerTenantIsolationEndpoint();
        // `13-02`: checkout-session creation (operator-authenticated) and the ЮKassa webhook receiver
        // (signature-authenticated, no RequireAuthorization policy) - see BillingEndpoints' own remarks for
        // why the webhook receiver lives on this host rather than Ago.Chat.Webhooks.
        app.MapBillingEndpoints();
        // `19-01`: operator-only "Suggest a reply" - see ReplyDraftEndpoints' own remarks for why it is
        // mapped directly rather than under any shared dual-scheme group.
        app.MapReplyDraftEndpoints();
    }

    // SignalR's WebSocket upgrade cannot carry an Authorization header, so the client passes the token
    // as ?access_token=... instead - restricted to this hub's own path, never accepted on ordinary
    // HTTP requests (the standard ASP.NET Core SignalR JWT pattern). Moved here verbatim from
    // Program.cs's own bottom-of-file local function - still only ever called from within
    // ConfigureServices above, for the two JwtBearer schemes configured there.
    private static JwtBearerEvents HubTokenFromQueryString(string hubPath) => new()
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
}
