var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness and readiness are the same trivial check for now - no real dependency (Postgres,
// RabbitMQ, Redis) is wired up yet to report on. They diverge once Stage 1+ adds one
// (docs/architecture/edge.md: readiness must go false while a dependency is unreachable or the
// node is draining; liveness must not).
app.MapHealthChecks("/healthz/live");
app.MapHealthChecks("/healthz/ready");

app.Run();
