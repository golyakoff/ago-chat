using Ago.Chat.FakeCrm;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// No real dependency to report on (out of scope: this harness keeps no persistence or delivery log
// of its own), so liveness and readiness are the same trivial check - the same shape
// Ago.Chat.Webhooks' own Program.cs uses before it has a real dependency to wire up.
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IClock, RealTimeClock>();

builder.Services
    .AddOptions<FakeCrmOptions>()
    .Bind(builder.Configuration.GetSection(FakeCrmOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningSecret),
        "Set FakeCrm:SigningSecret - a fake CRM that verifies against an empty secret proves nothing.")
    .Validate(o => o.SignatureToleranceSeconds > 0, "FakeCrm:SignatureToleranceSeconds must be positive.")
    .Validate(o => o.DisappearPort > 0, "FakeCrm:DisappearPort must be a valid port number.")
    .ValidateOnStart();

// `6-04`: the "disappears" personality - see DisappearedConnectionListener's own remarks for why it
// runs as a second raw listener instead of a branch inside the endpoint below.
builder.Services.AddHostedService<DisappearedConnectionListener>();

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = _ => false });

app.MapPost("/webhooks/deliver", HandleDeliverAsync);

app.Run();

static async Task<IResult> HandleDeliverAsync(HttpContext context, IOptions<FakeCrmOptions> options, IClock clock, CancellationToken ct)
{
    var rawBody = await ReadRawBodyAsync(context.Request, ct);

    // Every request is signature-checked, regardless of which personality it goes on to ask for -
    // the whole reason this check exists is to catch a real signing bug in whoever calls this
    // harness (this project's own remarks), not to prove this project's HMAC math, so there is no
    // personality that should be allowed to skip it.
    var verification = WebhookSignatureVerifier.Verify(
        rawBody,
        context.Request.Headers["X-Ago-Signature"],
        options.Value.SigningSecret,
        clock.UtcNow,
        TimeSpan.FromSeconds(options.Value.SignatureToleranceSeconds));

    if (verification != WebhookSignatureVerifier.VerificationResult.Valid)
    {
        return Results.Json(new { error = verification.ToString() }, statusCode: StatusCodes.Status401Unauthorized);
    }

    FakeCrmBehavior behavior;
    try
    {
        behavior = FakeCrmBehavior.Parse(context.Request.Headers["X-Fake-Crm-Behavior"]);
    }
    catch (FakeCrmBehaviorException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    switch (behavior.Kind)
    {
        case FakeCrmBehaviorKind.ServerError:
            return Results.StatusCode(behavior.StatusCode);

        case FakeCrmBehaviorKind.Hang:
            // ct is HttpContext.RequestAborted and nothing shorter is layered on top of it, so this
            // delay only ever ends two ways: the configured duration elapses (we go on to respond
            // 200 below), or the caller gives up first and the connection aborts. Either way it is
            // never this harness voluntarily ending the hang - the one rule the backlog names by name.
            await Task.Delay(behavior.HangDuration ?? Timeout.InfiniteTimeSpan, ct);
            return Results.Ok(new { received = true, heldFor = behavior.HangDuration });

        default:
            return Results.Ok(new { received = true });
    }
}

static async Task<byte[]> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, ct);
    return buffer.ToArray();
}

// Public and partial so Ago.Chat.FakeCrm.Tests can locate this assembly's built .dll via
// typeof(Program).Assembly.Location and launch it as a genuinely separate `dotnet` process - the
// standard ASP.NET Core pattern for making a top-level-statements Program reachable from another
// assembly, used here for process discovery rather than WebApplicationFactory<T>.
public partial class Program;
