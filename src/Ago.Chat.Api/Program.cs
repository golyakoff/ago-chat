using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Module;
using Ago.Platform.Kernel;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSignalR();

new ChatModule().ConfigureServices(builder.Services, builder.Configuration);

// 3-01: Ago.Chat.Api is the only host holding SignalR connections, so it is the only one that
// actually needs the heartbeat running - ChatModule registers the registry's DI surface for every
// host, but only resolving IConnectionRegistry (which this triggers) opens the Redis connection.
builder.Services.AddHostedService<ConnectionHeartbeat>();
builder.Services.AddSingleton<HubConnectionRegistration>();

// Generated fresh on every start, never configured or committed - consistent with "no secrets,
// ever" (repositories.md), even for a throwaway dev value. Tokens do not survive a restart, which
// is fine for a Stage 1 stub proving the shape (authorization.md), not a production concern -
// Stage 5's OIDC direction replaces this signing story outright, it does not evolve from it.
const string issuer = "ago-chat-api";
var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
builder.Services.AddSingleton(sp => new JwtTokenService(signingCredentials, issuer, sp.GetRequiredService<IClock>()));

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
        options.TokenValidationParameters = TokenValidationParametersFor(JwtSchemes.Operator);
        options.Events = HubTokenFromQueryString("/hubs/operator");
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Liveness and readiness are the same trivial check for now - no real dependency (Postgres,
// RabbitMQ, Redis) is wired up yet to report on. They diverge once Stage 1+ adds one
// (docs/architecture/edge.md: readiness must go false while a dependency is unreachable or the
// node is draining; liveness must not).
app.MapHealthChecks("/healthz/live");
app.MapHealthChecks("/healthz/ready");

app.MapAuthEndpoints();
app.MapHub<VisitorHub>("/hubs/visitor");
app.MapHub<OperatorHub>("/hubs/operator");

if (app.Environment.IsDevelopment())
{
    // The manual two-tab verification harness (1-06) - same-origin, so no CORS story is needed
    // for it. Real cross-origin widget CORS is Stage 5 (api-design.md).
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
