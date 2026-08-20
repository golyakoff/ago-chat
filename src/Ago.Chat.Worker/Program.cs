using Ago.Chat.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

// Liveness and readiness are the same trivial check for now - no real dependency (the broker) is
// wired up yet to report on (docs/architecture/edge.md).
app.MapHealthChecks("/healthz/live");
app.MapHealthChecks("/healthz/ready");

app.Run();
