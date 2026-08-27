using Ago.Chat.FakeMax;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services
    .AddOptions<FakeMaxOptions>()
    .Bind(builder.Configuration.GetSection(FakeMaxOptions.SectionName))
    .Validate(o => o.DefaultBehavior is "ok" or "500" or "hang", "FakeMax:DefaultBehavior must be ok, 500 or hang.")
    .ValidateOnStart();

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });

// MAX's own POST /messages?chat_id=... - MaxApiClient.SendMessageAsync's real target. No auth check
// here: this harness answers however its own DefaultBehavior says regardless of the Authorization
// header presented, because MaxChannelAdapterResilienceTests is exercising the resilience pipeline
// wrapped around the HTTP call, not MAX's own token validation.
app.MapPost("/messages", HandleSendMessageAsync);

// MAX's own POST /subscriptions - only ever reached by MaxApiClient.SubscribeWebhookAsync, not by the
// resilience tests, but present so a future test exercising that path has a real endpoint to call.
app.MapPost("/subscriptions", () => Results.Ok(new { }));

app.Run();

static async Task<IResult> HandleSendMessageAsync(IOptions<FakeMaxOptions> options, CancellationToken ct)
{
    switch (options.Value.DefaultBehavior)
    {
        case "500":
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        case "hang":
            // ct is HttpContext.RequestAborted - this only ever ends by the configured duration
            // elapsing or the caller giving up first, the same "never voluntarily ended" rule
            // Ago.Chat.FakeCrm's own Hang personality states.
            await Task.Delay(TimeSpan.FromSeconds(options.Value.HangSeconds), ct);
            return Results.Ok(new { message = new { body = new { mid = "fake-hung-through" } } });

        default:
            return Results.Ok(new { message = new { body = new { mid = "fake-message-1" } } });
    }
}

// Public and partial so Ago.Chat.FakeMax.Tests can locate this assembly's built .dll via
// typeof(Program).Assembly.Location - Ago.Chat.FakeCrm's own precedent.
public partial class Program;
