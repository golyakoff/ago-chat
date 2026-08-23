using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

// Every numeric :F1/:F2 interpolation below must format with InvariantCulture, not the machine's own
// culture - found live running this on a Russian-locale Windows box: the default culture's decimal
// comma collided with the CSV format's own comma delimiter, silently splitting one latency-ms column
// into two and shifting every field after it. The computed percentiles themselves were unaffected
// (pure double arithmetic, no culture involved) - only display/serialization was wrong - but a CSV a
// reviewer might open in Excel or pandas has to be right regardless of whose machine produced it.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// `6-06`: black-box message-traffic driver for the webhooks-isolation load proof. Talks only to the
// public HTTP/hub surface (visitor-sessions REST, /hubs/visitor, /hubs/operator, the same shape
// dev-harness.html and a real widget/console use) - see the .csproj header for why this is a real
// .NET SignalR client rather than a k6 script. Config is env vars, not a config file, matching this
// project's own "no secrets in a committed file" habit for anything credential-shaped, even test-only
// ones.

var apiVisitorBase = Environment.GetEnvironmentVariable("LOADDRIVER_VISITOR_API") ?? "http://localhost:5010";
var apiOperatorBase = Environment.GetEnvironmentVariable("LOADDRIVER_OPERATOR_API") ?? "http://localhost:5009";
var keycloakBase = Environment.GetEnvironmentVariable("LOADDRIVER_KEYCLOAK") ?? "http://127.0.0.1:8081";
var lanes = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_LANES") ?? "15");
var baselineSeconds = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_BASELINE_SECONDS") ?? "60");
var hungWindowSeconds = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_HUNG_SECONDS") ?? "150");
var sendIntervalMs = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_SEND_INTERVAL_MS") ?? "1000");
var recycleEverySeconds = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_RECYCLE_SECONDS") ?? "20");
var outputPath = Environment.GetEnvironmentVariable("LOADDRIVER_OUTPUT") ?? "load-driver-output.csv";
var markerPath = Environment.GetEnvironmentVariable("LOADDRIVER_MARKERS") ?? "load-driver-markers.txt";

const string PublicKey = "demo_site";

Console.WriteLine($"[driver] visitor api={apiVisitorBase} operator api={apiOperatorBase} lanes={lanes} " +
    $"baseline={baselineSeconds}s hungWindow={hungWindowSeconds}s interval={sendIntervalMs}ms recycle={recycleEverySeconds}s");

using var http = new HttpClient();

var operatorToken = await GetOperatorTokenAsync(http, keycloakBase);
Console.WriteLine("[driver] operator token acquired");

await using var operatorConnection = BuildHub($"{apiOperatorBase}/hubs/operator", operatorToken);

// Computed before the MessageReceived closure below is registered - a top-level-statement local
// captured by a closure must already be definitely assigned at the point the closure is *declared*,
// not merely by the time it could first run, or the compiler cannot prove the capture safe (found by
// building this: CS0170 "use of possibly unassigned field" on hungStart, since top-level locals
// captured by a lambda become fields of the synthesized entry-point type).
var runStart = DateTimeOffset.UtcNow;
var hungStart = runStart.AddSeconds(baselineSeconds);
var runEnd = hungStart.AddSeconds(hungWindowSeconds);

string CurrentPhase()
{
    var now = DateTimeOffset.UtcNow;
    return now < hungStart ? "baseline" : "hung-crm";
}

// clientMessageId -> (sendUtc, ackMs?) - populated by the sending lane, resolved by the operator's
// MessageReceived handler when the matching delivery lands. ConcurrentDictionary since sends and the
// one shared operator connection's receive callback run on different tasks.
var pending = new ConcurrentDictionary<Guid, DateTimeOffset>();
var records = new ConcurrentQueue<Record>();

operatorConnection.On<MessageDto>("MessageReceived", dto =>
{
    var receivedAt = DateTimeOffset.UtcNow;
    if (dto.ClientMessageId is { } id && pending.TryRemove(id, out var sentAt))
    {
        records.Enqueue(new Record(id, sentAt, receivedAt - sentAt, Phase: CurrentPhase()));
    }
});

await operatorConnection.StartAsync();
Console.WriteLine("[driver] operator connection started");

File.WriteAllText(markerPath,
    $"run_start_utc={runStart:O}\nhung_start_utc={hungStart:O}\nrun_end_utc={runEnd:O}\n");
Console.WriteLine($"[driver] run_start={runStart:O} hung_start={hungStart:O} run_end={runEnd:O}");
Console.WriteLine("[driver] >>> start Ago.Chat.WebhookDispatchRunner + hung Ago.Chat.FakeCrm at hung_start_utc above <<<");

var ackLatencies = new ConcurrentQueue<AckRecord>();
var errors = new ConcurrentQueue<string>();

var burstSize = int.Parse(Environment.GetEnvironmentVariable("LOADDRIVER_BULKHEAD_BURST") ?? "25");
var burstTask = RunBulkheadBurstAsync(burstSize);

var laneTasks = Enumerable.Range(0, lanes).Select(lane => RunLaneAsync(lane)).ToArray();
await Task.WhenAll([burstTask, .. laneTasks]);

await operatorConnection.StopAsync();

WriteCsv();
PrintSummary();

return;

async Task RunLaneAsync(int lane)
{
    var visitorConnection = await StartVisitorAsync(apiVisitorBase);
    var conversationId = visitorConnection.ConversationId;
    await AssignToOperatorAsync(operatorConnection, conversationId);
    Console.WriteLine($"[lane {lane}] conversation {conversationId} assigned");

    var laneEnd = runEnd;
    var nextRecycle = DateTimeOffset.UtcNow.AddSeconds(recycleEverySeconds + lane % 5);

    while (DateTimeOffset.UtcNow < laneEnd)
    {
        var clientMessageId = Guid.NewGuid();
        var sentAt = DateTimeOffset.UtcNow;
        pending[clientMessageId] = sentAt;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await visitorConnection.Connection.InvokeAsync<int>(
                "SendMessageAsync", visitorConnection.ConversationId, $"lane-{lane}-load-msg", null, clientMessageId);
            sw.Stop();
            ackLatencies.Enqueue(new AckRecord(clientMessageId, sentAt, sw.Elapsed, CurrentPhase()));
        }
        catch (Exception ex)
        {
            errors.Enqueue($"lane {lane} send failed: {ex.Message}");
            pending.TryRemove(clientMessageId, out _);
        }

        if (DateTimeOffset.UtcNow >= nextRecycle && DateTimeOffset.UtcNow < laneEnd.AddSeconds(-5))
        {
            try
            {
                await CloseConversationAsync(http, apiOperatorBase, operatorToken, conversationId);
                await visitorConnection.Connection.DisposeAsync();
                visitorConnection = await StartVisitorAsync(apiVisitorBase);
                conversationId = visitorConnection.ConversationId;
                await AssignToOperatorAsync(operatorConnection, conversationId);
                Console.WriteLine($"[lane {lane}] recycled -> {conversationId}");
            }
            catch (Exception ex)
            {
                errors.Enqueue($"lane {lane} recycle failed: {ex.Message}");
            }

            nextRecycle = DateTimeOffset.UtcNow.AddSeconds(recycleEverySeconds);
        }

        await Task.Delay(sendIntervalMs);
    }

    await visitorConnection.Connection.DisposeAsync();
}

void WriteCsv()
{
    using var writer = new StreamWriter(outputPath, append: false);
    writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
    foreach (var r in ackLatencies)
    {
        writer.WriteLine($"ack,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds:F2},{r.Phase}");
    }
    foreach (var r in records)
    {
        writer.WriteLine($"delivered,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds:F2},{r.Phase}");
    }
    foreach (var e in errors)
    {
        writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
    }
    Console.WriteLine($"[driver] wrote {outputPath}");
}

// `6-06`: the steady lane traffic above (recycling every ~20-45s) generates dispatch events too
// sparsely spaced to ever exceed the per-tenant bulkhead's MaxConcurrency(4)+MaxQueuedActions(16)=20
// slots at once - resilience.md's own bulkhead claim needs a genuine burst of concurrent deliveries
// for the *same* site to prove the cap is actually hit and holds, not just that the breaker opens
// under sustained sequential failures (already covered by the lanes' own steady churn). Fired once, at
// hung_start, before the breaker has had any chance to open - every one of these conversations'
// assign+close events therefore attempts a genuinely fresh delivery concurrently with every other one,
// which is what actually fills the bulkhead's queue rather than each attempt finding an already-open
// breaker and short-circuiting instantly.
async Task RunBulkheadBurstAsync(int size)
{
    var due = hungStart - DateTimeOffset.UtcNow;
    if (due > TimeSpan.Zero)
    {
        await Task.Delay(due);
    }

    Console.WriteLine($"[burst] firing {size} concurrent conversations to saturate the per-tenant bulkhead");
    var tasks = Enumerable.Range(0, size).Select(async i =>
    {
        try
        {
            var session = await StartVisitorAsync(apiVisitorBase);
            await AssignToOperatorAsync(operatorConnection, session.ConversationId);
            await CloseConversationAsync(http, apiOperatorBase, operatorToken, session.ConversationId);
            await session.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            errors.Enqueue($"burst {i} failed: {ex.Message}");
        }
    });
    await Task.WhenAll(tasks);
    Console.WriteLine("[burst] done");
}

void PrintSummary()
{
    void Report(string label, IEnumerable<double> values)
    {
        var arr = values.OrderBy(v => v).ToArray();
        if (arr.Length == 0)
        {
            Console.WriteLine($"{label}: no samples");
            return;
        }

        double Pct(double p) => arr[Math.Min(arr.Length - 1, (int)Math.Ceiling(p * arr.Length) - 1)];
        Console.WriteLine($"{label}: n={arr.Length} p50={Pct(0.50):F1}ms p95={Pct(0.95):F1}ms p99={Pct(0.99):F1}ms max={arr[^1]:F1}ms");
    }

    Console.WriteLine();
    Console.WriteLine("=== send -> ack ===");
    Report("baseline", ackLatencies.Where(r => r.Phase == "baseline").Select(r => r.Latency.TotalMilliseconds));
    Report("hung-crm", ackLatencies.Where(r => r.Phase == "hung-crm").Select(r => r.Latency.TotalMilliseconds));

    Console.WriteLine("=== send -> delivered (operator, cross-node) ===");
    Report("baseline", records.Where(r => r.Phase == "baseline").Select(r => r.Latency.TotalMilliseconds));
    Report("hung-crm", records.Where(r => r.Phase == "hung-crm").Select(r => r.Latency.TotalMilliseconds));

    Console.WriteLine($"errors: {errors.Count}");
    foreach (var e in errors.Take(20))
    {
        Console.WriteLine($"  {e}");
    }
}

async Task<string> GetOperatorTokenAsync(HttpClient client, string keycloak)
{
    var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "password",
        ["client_id"] = "ago-console",
        ["username"] = "demo-operator",
        ["password"] = "demo-operator-password",
    });
    var res = await client.PostAsync($"{keycloak}/realms/ago-chat/protocol/openid-connect/token", form);
    res.EnsureSuccessStatusCode();
    var body = await res.Content.ReadFromJsonAsync<JsonElement>();
    return body.GetProperty("access_token").GetString()!;
}

async Task<VisitorSession> StartVisitorAsync(string apiBase)
{
    var res = await http.PostAsJsonAsync($"{apiBase}/api/v1/visitor-sessions", new { publicKey = PublicKey });
    res.EnsureSuccessStatusCode();
    var body = await res.Content.ReadFromJsonAsync<JsonElement>();
    var token = body.GetProperty("token").GetString()!;

    var connection = BuildHub($"{apiBase}/hubs/visitor", token);
    await connection.StartAsync();
    var join = await connection.InvokeAsync<VisitorJoinResult>("JoinAsync", (int?)null);
    return new VisitorSession(connection, join.ConversationId);
}

async Task AssignToOperatorAsync(HubConnection operatorConn, Guid conversationId)
{
    await operatorConn.InvokeAsync<HistoryPage>("JoinConversationAsync", conversationId, (int?)null);
}

async Task CloseConversationAsync(HttpClient client, string apiBase, string token, Guid conversationId)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/api/v1/conversations/{conversationId}/close");
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var res = await client.SendAsync(req);
    res.EnsureSuccessStatusCode();
}

HubConnection BuildHub(string url, string token) =>
    new HubConnectionBuilder()
        .WithUrl($"{url}?access_token={token}", options =>
        {
            options.SkipNegotiation = true;
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
        })
        .Build();

internal sealed record VisitorSession(HubConnection Connection, Guid ConversationId);

internal sealed record MessageDto(
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTimeOffset CreatedAt,
    Guid? AttachmentId, Guid? ClientMessageId, Guid? ConversationId);

internal sealed record VisitorJoinResult(Guid ConversationId, bool IsNew, IReadOnlyList<MessageDto> History);

internal sealed record HistoryPage(IReadOnlyList<MessageDto> Messages, int? NextBeforeSequence);

internal sealed record Record(Guid ClientMessageId, DateTimeOffset SentAt, TimeSpan Latency, string Phase);

internal sealed record AckRecord(Guid ClientMessageId, DateTimeOffset SentAt, TimeSpan Latency, string Phase);
