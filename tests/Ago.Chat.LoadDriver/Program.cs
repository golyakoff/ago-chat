using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

// Every numeric :F1/:F2 interpolation below must format with InvariantCulture, not the machine's own
// culture - found live running this on a Russian-locale Windows box during `6-06` (see that item's
// own note in git history): the default culture's decimal comma collided with the CSV format's own
// comma delimiter, silently splitting one latency-ms column into two and shifting every field after
// it. The computed percentiles themselves were unaffected (pure double arithmetic, no culture
// involved) - only display/serialization was wrong - but a CSV a reviewer might open in Excel or
// pandas has to be right regardless of whose machine produced it.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// `7-04`: this driver started life in `6-06` as a single-purpose hung-webhook load proof. This
// change generalizes it into a real black-box load driver with six named scenarios, one per `7-04`'s
// own scope, dispatched by `LOADDRIVER_SCENARIO` - still talking only to the public HTTP/hub surface
// (visitor-sessions REST, /hubs/visitor, /hubs/operator, attachments REST), still the real
// `Microsoft.AspNetCore.SignalR.Client` package, for the identical reason `6-06` already stated in
// this file's own git history and restated in `.csproj`: k6 was not available/installable in this
// unattended session, and hand-rolling SignalR's wire framing in k6 would measure a reimplementation,
// not the real system. `6-06`'s own original hung-webhook scenario is preserved as
// `webhook-isolation` below, unchanged in behaviour, so that item's own report stays reproducible.

var scenario = Environment.GetEnvironmentVariable("LOADDRIVER_SCENARIO")
    ?? throw new InvalidOperationException(
        "LOADDRIVER_SCENARIO is required: webhook-isolation | steady-ingest | burst-ingest | " +
        "connection-storm | capacity-ramp | reconnect-storm | assignment-contention | attachment-presign");

var apiVisitorBase = Environment.GetEnvironmentVariable("LOADDRIVER_VISITOR_API") ?? "http://localhost:5110";
var apiOperatorBase = Environment.GetEnvironmentVariable("LOADDRIVER_OPERATOR_API") ?? "http://localhost:5109";
var keycloakBase = Environment.GetEnvironmentVariable("LOADDRIVER_KEYCLOAK") ?? "http://127.0.0.1:8081";
var outputPath = Environment.GetEnvironmentVariable("LOADDRIVER_OUTPUT") ?? "load-driver-output.csv";
var markerPath = Environment.GetEnvironmentVariable("LOADDRIVER_MARKERS") ?? "load-driver-markers.txt";

const string PublicKey = "demo_site";

Console.WriteLine($"[driver] scenario={scenario} visitor api={apiVisitorBase} operator api={apiOperatorBase}");

using var http = new HttpClient();

switch (scenario)
{
    case "webhook-isolation":
        await RunWebhookIsolationAsync();
        break;
    case "steady-ingest":
        await RunSteadyIngestAsync();
        break;
    case "burst-ingest":
        await RunBurstIngestAsync();
        break;
    case "connection-storm":
        await RunConnectionStormAsync();
        break;
    case "capacity-ramp":
        await RunCapacityRampAsync();
        break;
    case "reconnect-storm":
        await RunReconnectStormAsync();
        break;
    case "assignment-contention":
        await RunAssignmentContentionAsync();
        break;
    case "attachment-presign":
        await RunAttachmentPresignAsync();
        break;
    default:
        throw new InvalidOperationException($"Unknown LOADDRIVER_SCENARIO '{scenario}'.");
}

return;

// ============================================================================================
// Scenario 1: steady ingest - N lanes, fixed send interval, long plateau. Warm-up discarded.
// ============================================================================================
async Task RunSteadyIngestAsync()
{
    var lanes = IntEnv("LOADDRIVER_LANES", 40);
    var intervalMs = IntEnv("LOADDRIVER_INTERVAL_MS", 1000);
    var warmupSeconds = IntEnv("LOADDRIVER_WARMUP_SECONDS", 60);
    var measureSeconds = IntEnv("LOADDRIVER_MEASURE_SECONDS", 240);

    Console.WriteLine($"[steady-ingest] lanes={lanes} interval={intervalMs}ms warmup={warmupSeconds}s " +
        $"measure={measureSeconds}s target-rate~={1000.0 / intervalMs * lanes:F1}msg/s");

    var operatorToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-operator", "demo-operator-password");
    await using var operatorConnection = BuildHub($"{apiOperatorBase}/hubs/operator", operatorToken, autoReconnect: false);

    var runStart = DateTimeOffset.UtcNow;
    var measureStart = runStart.AddSeconds(warmupSeconds);
    var runEnd = measureStart.AddSeconds(measureSeconds);

    string Phase(DateTimeOffset now) => now < measureStart ? "warmup" : "measured";

    var pending = new ConcurrentDictionary<Guid, DateTimeOffset>();
    var delivered = new ConcurrentQueue<Record>();
    var acks = new ConcurrentQueue<AckRecord>();
    var errors = new ConcurrentQueue<string>();

    operatorConnection.On<MessageDto>("MessageReceived", dto =>
    {
        var receivedAt = DateTimeOffset.UtcNow;
        if (dto.ClientMessageId is { } id && pending.TryRemove(id, out var sentAt))
        {
            delivered.Enqueue(new Record(id, sentAt, receivedAt - sentAt, Phase(receivedAt)));
        }
    });

    await operatorConnection.StartAsync();
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\nmeasure_start_utc={measureStart:O}\nrun_end_utc={runEnd:O}\n");
    Console.WriteLine($"[steady-ingest] run_start={runStart:O} measure_start={measureStart:O} run_end={runEnd:O}");

    async Task LaneAsync(int lane)
    {
        var visitor = await StartVisitorAsync(apiVisitorBase);
        await AssignToOperatorAsync(operatorConnection, visitor.ConversationId);

        while (DateTimeOffset.UtcNow < runEnd)
        {
            var clientMessageId = Guid.NewGuid();
            var sentAt = DateTimeOffset.UtcNow;
            pending[clientMessageId] = sentAt;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await visitor.Connection.InvokeAsync<int>(
                    "SendMessageAsync", visitor.ConversationId, $"lane-{lane}-steady", null, clientMessageId);
                sw.Stop();
                acks.Enqueue(new AckRecord(clientMessageId, sentAt, sw.Elapsed, Phase(sentAt)));
            }
            catch (Exception ex)
            {
                errors.Enqueue($"lane {lane} send failed: {ex.Message}");
                pending.TryRemove(clientMessageId, out _);
            }

            await Task.Delay(intervalMs);
        }

        await visitor.Connection.DisposeAsync();
    }

    await Task.WhenAll(Enumerable.Range(0, lanes).Select(LaneAsync));
    await operatorConnection.StopAsync();

    WriteLatencyCsv(outputPath, acks, delivered, errors);
    Console.WriteLine();
    Console.WriteLine("=== send -> ack (steady ingest) ===");
    ReportPercentiles("warmup (discarded)", acks.Where(r => r.Phase == "warmup").Select(r => r.Latency.TotalMilliseconds));
    ReportPercentiles("measured", acks.Where(r => r.Phase == "measured").Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine("=== send -> delivered, cross-node ===");
    ReportPercentiles("warmup (discarded)", delivered.Where(r => r.Phase == "warmup").Select(r => r.Latency.TotalMilliseconds));
    ReportPercentiles("measured", delivered.Where(r => r.Phase == "measured").Select(r => r.Latency.TotalMilliseconds));
    var measuredAcks = acks.Count(r => r.Phase == "measured");
    Console.WriteLine($"measured-window throughput ~= {measuredAcks / (double)measureSeconds:F2} msg/s (n={measuredAcks} over {measureSeconds}s)");
    Console.WriteLine($"errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// Scenario 2: burst ingest - calm baseline, then a short burst at a much higher rate, then
// cooldown. Same lane pool throughout; only the per-lane send interval changes by phase, so
// connection count stays constant and only offered load changes.
// ============================================================================================
async Task RunBurstIngestAsync()
{
    var lanes = IntEnv("LOADDRIVER_BURST_LANES", 40);
    var calmIntervalMs = IntEnv("LOADDRIVER_CALM_INTERVAL_MS", 3000);
    var burstIntervalMs = IntEnv("LOADDRIVER_BURST_INTERVAL_MS", 300);
    var baselineSeconds = IntEnv("LOADDRIVER_BASELINE_SECONDS", 30);
    var burstSeconds = IntEnv("LOADDRIVER_BURST_SECONDS", 30);
    var cooldownSeconds = IntEnv("LOADDRIVER_COOLDOWN_SECONDS", 30);

    var calmRate = 1000.0 / calmIntervalMs * lanes;
    var burstRate = 1000.0 / burstIntervalMs * lanes;
    Console.WriteLine($"[burst-ingest] lanes={lanes} calm~={calmRate:F1}msg/s burst~={burstRate:F1}msg/s " +
        $"baseline={baselineSeconds}s burst={burstSeconds}s cooldown={cooldownSeconds}s");

    var operatorToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-operator", "demo-operator-password");
    await using var operatorConnection = BuildHub($"{apiOperatorBase}/hubs/operator", operatorToken, autoReconnect: false);

    var runStart = DateTimeOffset.UtcNow;
    var burstStart = runStart.AddSeconds(baselineSeconds);
    var burstEnd = burstStart.AddSeconds(burstSeconds);
    var runEnd = burstEnd.AddSeconds(cooldownSeconds);

    string Phase(DateTimeOffset now) => now < burstStart ? "baseline" : now < burstEnd ? "burst" : "cooldown";
    int IntervalFor(DateTimeOffset now) => Phase(now) == "burst" ? burstIntervalMs : calmIntervalMs;

    var pending = new ConcurrentDictionary<Guid, DateTimeOffset>();
    var delivered = new ConcurrentQueue<Record>();
    var acks = new ConcurrentQueue<AckRecord>();
    var errors = new ConcurrentQueue<string>();

    operatorConnection.On<MessageDto>("MessageReceived", dto =>
    {
        var receivedAt = DateTimeOffset.UtcNow;
        if (dto.ClientMessageId is { } id && pending.TryRemove(id, out var sentAt))
        {
            delivered.Enqueue(new Record(id, sentAt, receivedAt - sentAt, Phase(receivedAt)));
        }
    });

    await operatorConnection.StartAsync();
    File.WriteAllText(markerPath,
        $"run_start_utc={runStart:O}\nburst_start_utc={burstStart:O}\nburst_end_utc={burstEnd:O}\nrun_end_utc={runEnd:O}\n");
    Console.WriteLine($"[burst-ingest] run_start={runStart:O} burst_start={burstStart:O} burst_end={burstEnd:O} run_end={runEnd:O}");

    async Task LaneAsync(int lane)
    {
        var visitor = await StartVisitorAsync(apiVisitorBase);
        await AssignToOperatorAsync(operatorConnection, visitor.ConversationId);

        while (DateTimeOffset.UtcNow < runEnd)
        {
            var clientMessageId = Guid.NewGuid();
            var sentAt = DateTimeOffset.UtcNow;
            pending[clientMessageId] = sentAt;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await visitor.Connection.InvokeAsync<int>(
                    "SendMessageAsync", visitor.ConversationId, $"lane-{lane}-burst", null, clientMessageId);
                sw.Stop();
                acks.Enqueue(new AckRecord(clientMessageId, sentAt, sw.Elapsed, Phase(sentAt)));
            }
            catch (Exception ex)
            {
                errors.Enqueue($"lane {lane} send failed: {ex.Message}");
                pending.TryRemove(clientMessageId, out _);
            }

            await Task.Delay(IntervalFor(DateTimeOffset.UtcNow));
        }

        await visitor.Connection.DisposeAsync();
    }

    await Task.WhenAll(Enumerable.Range(0, lanes).Select(LaneAsync));
    await operatorConnection.StopAsync();

    WriteLatencyCsv(outputPath, acks, delivered, errors);
    Console.WriteLine();
    foreach (var phase in new[] { "baseline", "burst", "cooldown" })
    {
        Console.WriteLine($"=== send -> ack, {phase} ===");
        ReportPercentiles(phase, acks.Where(r => r.Phase == phase).Select(r => r.Latency.TotalMilliseconds));
    }
    foreach (var phase in new[] { "baseline", "burst", "cooldown" })
    {
        Console.WriteLine($"=== send -> delivered, {phase} ===");
        ReportPercentiles(phase, delivered.Where(r => r.Phase == phase).Select(r => r.Latency.TotalMilliseconds));
    }
    var burstAcks = acks.Count(r => r.Phase == "burst");
    Console.WriteLine($"burst-window observed throughput ~= {burstAcks / (double)burstSeconds:F2} msg/s (n={burstAcks} over {burstSeconds}s, offered ~{burstRate:F1}msg/s)");
    Console.WriteLine($"errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// Scenario 3: connection storm - ramp to N concurrent idle WebSocket connections, hold, measure
// connect-time distribution. No message traffic (isolates connection-scale effects from
// ingest-pipeline effects, which scenarios 1/2 already cover). Pair with resource-monitor.ps1
// against the Api process pids for the memory/GC side of this.
// ============================================================================================
async Task RunConnectionStormAsync()
{
    var target = IntEnv("LOADDRIVER_TARGET_CONNECTIONS", 300);
    var rampSeconds = IntEnv("LOADDRIVER_RAMP_SECONDS", 60);
    var holdSeconds = IntEnv("LOADDRIVER_HOLD_SECONDS", 90);
    var rampConcurrency = IntEnv("LOADDRIVER_RAMP_CONCURRENCY", 10);

    Console.WriteLine($"[connection-storm] target={target} ramp={rampSeconds}s hold={holdSeconds}s concurrency={rampConcurrency}");

    var runStart = DateTimeOffset.UtcNow;
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\ntarget_connections={target}\nramp_seconds={rampSeconds}\nhold_seconds={holdSeconds}\n");

    var connectTimes = new ConcurrentQueue<double>();
    var errors = new ConcurrentQueue<string>();
    var connections = new ConcurrentBag<HubConnection>();
    var perConnectionDelay = TimeSpan.FromMilliseconds(rampSeconds * 1000.0 / target);

    var gate = new SemaphoreSlim(rampConcurrency);
    var connectTasks = Enumerable.Range(0, target).Select(async i =>
    {
        await Task.Delay(TimeSpan.FromTicks(perConnectionDelay.Ticks * i));
        await gate.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var visitor = await StartVisitorAsync(apiVisitorBase);
            sw.Stop();
            connectTimes.Enqueue(sw.Elapsed.TotalMilliseconds);
            connections.Add(visitor.Connection);
        }
        catch (Exception ex)
        {
            errors.Enqueue($"connection {i} failed: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    });
    await Task.WhenAll(connectTasks);

    var rampDone = DateTimeOffset.UtcNow;
    Console.WriteLine($"[connection-storm] ramp complete at {rampDone:O}: {connections.Count}/{target} connected, {errors.Count} failed");

    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    var holdDone = DateTimeOffset.UtcNow;
    Console.WriteLine($"[connection-storm] hold complete at {holdDone:O}, tearing down");

    var stillOpen = 0;
    foreach (var c in connections)
    {
        if (c.State == HubConnectionState.Connected) stillOpen++;
        await c.DisposeAsync();
    }

    using (var writer = new StreamWriter(outputPath, append: false))
    {
        writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
        foreach (var ms in connectTimes)
        {
            writer.WriteLine($"connect,,,{ms.ToString("F2", CultureInfo.InvariantCulture)},ramp");
        }
        foreach (var e in errors)
        {
            writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== connect time (visitor-session POST + hub negotiate + start) ===");
    ReportPercentiles("ramp", connectTimes);
    Console.WriteLine($"connected: {connections.Count}/{target}, still open at teardown: {stillOpen}/{connections.Count}, errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// Scenario 4: capacity ramp - escalating step ramp of concurrently open conversations, each held
// open with light, realistic activity (an occasional visitor message - what an operator's own
// "waiting for messages" open dialog actually looks like, not the silent idle socket
// connection-storm above deliberately holds). connection-storm isolates pure connection-count
// effects from the ingest pipeline on purpose; this scenario asks the question this item actually
// needs answered - how many simultaneously open conversations the deployment can carry before
// something breaks, and what breaks first, with connection-scale and a steady trickle of message
// traffic present together, the way a real day of visitor traffic looks. Connections accumulate
// step over step and are never torn down early - each step's target is cumulative, not a fresh
// batch - so a later step's numbers reflect every earlier step's connections still doing their own
// light messaging, not just the newest ones. Each step's own error rate (connect failures,
// message-send failures, both measured against that step's own hold window) is checked once the
// hold completes; crossing LOADDRIVER_MAX_ERROR_RATE stops the ramp from escalating further but
// never aborts the step already in progress - the point is to find the ceiling unattended and
// safely, not to leave a human watching a dashboard through every step in real time by hand. The
// same reasoning nfr.md already applies to production alerting applies to a driver aimed at a
// deployment someone else may be relying on: the stop condition has to be mechanical, because a
// person is not guaranteed to be watching the console at the exact moment a step goes bad.
// Deliberately does not assign these conversations to an operator - that is assignment-
// contention's own separate concern, kept apart the same way connection-storm keeps
// connection-scale apart from ingest-scale.
// ============================================================================================
async Task RunCapacityRampAsync()
{
    var stepSize = IntEnv("LOADDRIVER_STEP_SIZE", 500);
    var stepCount = IntEnv("LOADDRIVER_STEP_COUNT", 10);
    var stepHoldSeconds = IntEnv("LOADDRIVER_STEP_HOLD_SECONDS", 60);
    var rampConcurrency = IntEnv("LOADDRIVER_RAMP_CONCURRENCY", 10);
    var messageIntervalSeconds = IntEnv("LOADDRIVER_MESSAGE_INTERVAL_SECONDS", 45);
    var maxErrorRate = DoubleEnv("LOADDRIVER_MAX_ERROR_RATE", 0.05);

    Console.WriteLine($"[capacity-ramp] stepSize={stepSize} stepCount={stepCount} holdSeconds={stepHoldSeconds} " +
        $"rampConcurrency={rampConcurrency} messageInterval~={messageIntervalSeconds}s " +
        $"maxErrorRate={maxErrorRate:F2} maxTarget={stepSize * stepCount}");

    var runStart = DateTimeOffset.UtcNow;
    File.WriteAllText(markerPath,
        $"run_start_utc={runStart:O}\nstep_size={stepSize}\nstep_count={stepCount}\nstep_hold_seconds={stepHoldSeconds}\n" +
        $"max_error_rate={maxErrorRate:F2}\n");

    using var cts = new CancellationTokenSource();
    var gate = new SemaphoreSlim(rampConcurrency);
    var connections = new ConcurrentBag<VisitorSession>();
    var messageLoopTasks = new ConcurrentBag<Task>();
    var connectRecords = new ConcurrentQueue<(double Ms, int Step)>();
    var connectErrorRecords = new ConcurrentQueue<(string Msg, int Step)>();
    var acks = new ConcurrentQueue<AckRecord>();
    var messageErrorRecords = new ConcurrentQueue<(string Msg, int Step)>();

    // Read by every open connection's own message loop to label the ack (or error) it is about to
    // record. Plain `int`, not `Interlocked`/`Volatile` - reads and writes of a word-sized field
    // are already atomic on every platform this runs on, and the only thing this variable drives
    // is a human-readable step label on a sample, not a control-flow decision, so a few
    // milliseconds of cross-thread staleness around a step boundary is harmless.
    var currentStep = 0;

    async Task MessageLoopAsync(VisitorSession visitor, string messageTag)
    {
        // Jittered so thousands of held-open conversations don't all fire on the same clock tick -
        // that would itself synthesize an artificial burst, exactly what burst-ingest's own
        // deliberate burst scenario exists to study on purpose, not what this scenario is for.
        while (!cts.IsCancellationRequested)
        {
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4; // +/-20%
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(messageIntervalSeconds * jitter), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var clientMessageId = Guid.NewGuid();
            var sentAt = DateTimeOffset.UtcNow;
            var step = currentStep;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await visitor.Connection.InvokeAsync<int>(
                    "SendMessageAsync", visitor.ConversationId, messageTag, null, clientMessageId);
                sw.Stop();
                acks.Enqueue(new AckRecord(clientMessageId, sentAt, sw.Elapsed, $"step{step}"));
            }
            catch (Exception ex)
            {
                messageErrorRecords.Enqueue(($"step{step} message send failed: {ex.Message}", step));
            }
        }
    }

    async Task OpenOneAsync(int step, int index)
    {
        await gate.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            VisitorSession visitor;
            try
            {
                visitor = await StartVisitorAsync(apiVisitorBase);
            }
            catch (Exception ex)
            {
                connectErrorRecords.Enqueue(($"step{step} connection {index} failed: {ex.Message}", step));
                return;
            }
            sw.Stop();
            connectRecords.Enqueue((sw.Elapsed.TotalMilliseconds, step));
            connections.Add(visitor);
            messageLoopTasks.Add(MessageLoopAsync(visitor, $"step{step}-conn{index}-capacity-ping"));
        }
        finally
        {
            gate.Release();
        }
    }

    var reachedStep = 0;
    var safetyValveTripped = false;
    string? safetyValveReason = null;

    for (var n = 1; n <= stepCount; n++)
    {
        var cumulativeTarget = n * stepSize;
        Console.WriteLine($"[capacity-ramp] step {n}/{stepCount}: opening {stepSize} new connections (cumulative target {cumulativeTarget})");

        await Task.WhenAll(Enumerable.Range(0, stepSize).Select(i => OpenOneAsync(n, i)));

        var rampDone = DateTimeOffset.UtcNow;
        var stepConnects = connectRecords.Where(r => r.Step == n).ToList();
        var stepConnectErrors = connectErrorRecords.Where(r => r.Step == n).ToList();
        var connectAttempts = stepConnects.Count + stepConnectErrors.Count;
        var connectErrorRate = connectAttempts == 0 ? 0.0 : stepConnectErrors.Count / (double)connectAttempts;

        File.AppendAllText(markerPath,
            $"step{n}_ramp_complete_utc={rampDone:O} cumulative_target={cumulativeTarget} " +
            $"cumulative_connections={connections.Count} step_connect_errors={stepConnectErrors.Count}\n");

        Console.WriteLine($"[capacity-ramp] step {n} ramp complete at {rampDone:O}: {connections.Count} cumulative connections, " +
            $"{stepConnectErrors.Count}/{connectAttempts} connect errors this step ({connectErrorRate * 100:F1}%)");
        ReportPercentiles($"step{n}-connect", stepConnects.Select(r => r.Ms));

        // Only from here does a message loop's own read of `currentStep` attribute an ack (or a
        // send failure) to this step - so "this step's message stats" below means exactly this
        // step's hold window, not the ramp that preceded it.
        currentStep = n;

        Console.WriteLine($"[capacity-ramp] step {n} holding at {connections.Count} connections for {stepHoldSeconds}s");
        await Task.Delay(TimeSpan.FromSeconds(stepHoldSeconds));

        var holdDone = DateTimeOffset.UtcNow;
        var stepAcks = acks.Where(r => r.Phase == $"step{n}").ToList();
        var stepMessageErrors = messageErrorRecords.Where(r => r.Step == n).ToList();
        var messageAttempts = stepAcks.Count + stepMessageErrors.Count;
        var messageErrorRate = messageAttempts == 0 ? 0.0 : stepMessageErrors.Count / (double)messageAttempts;

        File.AppendAllText(markerPath,
            $"step{n}_hold_complete_utc={holdDone:O} cumulative_connections={connections.Count} " +
            $"step_message_errors={stepMessageErrors.Count} step_message_attempts={messageAttempts}\n");

        Console.WriteLine($"[capacity-ramp] step {n} hold complete at {holdDone:O}: {stepMessageErrors.Count}/{messageAttempts} " +
            $"message errors this step ({messageErrorRate * 100:F1}%)");
        ReportPercentiles($"step{n}-ack", stepAcks.Select(r => r.Latency.TotalMilliseconds));

        reachedStep = n;

        if (connectErrorRate > maxErrorRate || messageErrorRate > maxErrorRate)
        {
            safetyValveTripped = true;
            safetyValveReason = $"step {n}: connect error rate {connectErrorRate:F3} or message error rate " +
                $"{messageErrorRate:F3} exceeded max {maxErrorRate:F3}";
            Console.WriteLine($"[capacity-ramp] SAFETY VALVE TRIPPED: {safetyValveReason} - holding here, not escalating further");
            break;
        }
    }

    Console.WriteLine("[capacity-ramp] tearing down");
    cts.Cancel();
    await Task.WhenAll(messageLoopTasks);

    var stillOpen = 0;
    foreach (var visitor in connections)
    {
        if (visitor.Connection.State == HubConnectionState.Connected) stillOpen++;
        await visitor.Connection.DisposeAsync();
    }

    using (var writer = new StreamWriter(outputPath, append: false))
    {
        writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
        foreach (var (ms, step) in connectRecords)
        {
            writer.WriteLine($"connect,,,{ms.ToString("F2", CultureInfo.InvariantCulture)},step{step}");
        }
        foreach (var r in acks)
        {
            writer.WriteLine($"ack,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)},{r.Phase}");
        }
        foreach (var (msg, _) in connectErrorRecords)
        {
            writer.WriteLine($"error,,,,{msg.Replace(',', ';')}");
        }
        foreach (var (msg, _) in messageErrorRecords)
        {
            writer.WriteLine($"error,,,,{msg.Replace(',', ';')}");
        }
    }
    Console.WriteLine($"[driver] wrote {outputPath}");

    File.AppendAllText(markerPath,
        $"run_end_utc={DateTimeOffset.UtcNow:O} reached_step={reachedStep} cumulative_connections={connections.Count} " +
        $"still_open_at_teardown={stillOpen} safety_valve_tripped={safetyValveTripped}\n");

    Console.WriteLine();
    Console.WriteLine("=== capacity-ramp summary ===");
    Console.WriteLine($"steps reached: {reachedStep}/{stepCount}, final cumulative connections: {connections.Count} " +
        $"(still open at teardown: {stillOpen})");
    Console.WriteLine(safetyValveTripped
        ? $"safety valve fired: {safetyValveReason}"
        : "safety valve did not fire - ramp completed all configured steps within the error-rate budget");
    Console.WriteLine("=== connect time, all steps ===");
    ReportPercentiles("overall", connectRecords.Select(r => r.Ms));
    Console.WriteLine("=== send -> ack, all steps ===");
    ReportPercentiles("overall", acks.Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine($"total connect errors: {connectErrorRecords.Count}, total message errors: {messageErrorRecords.Count}");
    foreach (var (msg, _) in connectErrorRecords.Take(20)) Console.WriteLine($"  {msg}");
    foreach (var (msg, _) in messageErrorRecords.Take(20)) Console.WriteLine($"  {msg}");
}

// ============================================================================================
// Scenario 5: reconnect storm - steady traffic through an external rolling restart of the
// visitor-node Api process (orchestrated by the caller's shell script, not this process - see
// this item's report for the exact timing). Uses SignalR's automatic reconnect; at the end,
// paginates each lane's full history and verifies the sequence set is contiguous with no gaps
// and no duplicates against every locally-recorded acknowledged clientMessageId - the concrete,
// checkable form of nfr.md's "zero acknowledged-but-lost messages".
// ============================================================================================
async Task RunReconnectStormAsync()
{
    var lanes = IntEnv("LOADDRIVER_LANES", 20);
    var intervalMs = IntEnv("LOADDRIVER_INTERVAL_MS", 1000);
    var totalSeconds = IntEnv("LOADDRIVER_TOTAL_SECONDS", 200);

    Console.WriteLine($"[reconnect-storm] lanes={lanes} interval={intervalMs}ms total={totalSeconds}s " +
        "(external restart of the visitor-node Api is orchestrated by the caller mid-run)");

    var operatorToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-operator", "demo-operator-password");
    await using var operatorConnection = BuildHub($"{apiOperatorBase}/hubs/operator", operatorToken, autoReconnect: false);
    await operatorConnection.StartAsync();

    var runStart = DateTimeOffset.UtcNow;
    var runEnd = runStart.AddSeconds(totalSeconds);
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\nrun_end_utc={runEnd:O}\n");
    Console.WriteLine($"[reconnect-storm] run_start={runStart:O} run_end={runEnd:O} - restart the visitor-node Api now per the schedule");

    var acks = new ConcurrentQueue<AckRecord>();
    var errors = new ConcurrentQueue<string>();
    var reconnectEvents = new ConcurrentQueue<(int Lane, DateTimeOffset At)>();
    var laneAcked = new ConcurrentDictionary<int, List<(Guid ClientMessageId, int Sequence)>>();
    var laneConversation = new ConcurrentDictionary<int, (HubConnection Connection, Guid ConversationId)>();

    async Task LaneAsync(int lane)
    {
        var visitor = await StartVisitorAsync(apiVisitorBase, autoReconnect: true);
        await AssignToOperatorAsync(operatorConnection, visitor.ConversationId);
        laneConversation[lane] = (visitor.Connection, visitor.ConversationId);
        laneAcked[lane] = [];

        visitor.Connection.Reconnected += _ =>
        {
            reconnectEvents.Enqueue((lane, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        };

        while (DateTimeOffset.UtcNow < runEnd)
        {
            var clientMessageId = Guid.NewGuid();
            var sentAt = DateTimeOffset.UtcNow;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var sequence = await visitor.Connection.InvokeAsync<int>(
                    "SendMessageAsync", visitor.ConversationId, $"lane-{lane}-reconnect", null, clientMessageId);
                sw.Stop();
                acks.Enqueue(new AckRecord(clientMessageId, sentAt, sw.Elapsed, "steady"));
                laneAcked[lane].Add((clientMessageId, sequence));
            }
            catch (Exception ex)
            {
                // Expected during the restart window - the connection is down or reconnecting.
                // Not counted as an ack-latency sample; counted here for the outage-duration view.
                errors.Enqueue($"lane {lane} send failed at {sentAt:O}: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(intervalMs);
        }
    }

    await Task.WhenAll(Enumerable.Range(0, lanes).Select(LaneAsync));
    var runEndActual = DateTimeOffset.UtcNow;
    Console.WriteLine($"[reconnect-storm] steady-traffic phase done at {runEndActual:O}, verifying history per lane");

    // End-of-run reconciliation: page through each lane's own full history and confirm every
    // sequence this lane was told it acked (SendMessageAsync's own return value, matching
    // nfr.md's "send -> ack" row) shows up exactly once, and the sequence set has no gap.
    var reconciliation = new List<string>();
    foreach (var (lane, (connection, conversationId)) in laneConversation)
    {
        try
        {
            var seenSequences = new List<int>();
            int? before = null;
            do
            {
                var page = await connection.InvokeAsync<HistoryPage>("GetHistoryAsync", conversationId, before, 100);
                seenSequences.AddRange(page.Messages.Select(m => m.Sequence));
                before = page.NextBeforeSequence;
            } while (before is not null);

            var ackedSequences = laneAcked[lane].Select(a => a.Sequence).OrderBy(s => s).ToList();
            var missing = ackedSequences.Except(seenSequences).ToList();
            var duplicates = seenSequences.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            if (missing.Count > 0 || duplicates.Count > 0)
            {
                reconciliation.Add($"lane {lane}: MISMATCH - {missing.Count} acked-but-missing sequence(s) " +
                    $"[{string.Join(",", missing.Take(10))}], {duplicates.Count} duplicate sequence(s) [{string.Join(",", duplicates.Take(10))}]");
            }
            else
            {
                reconciliation.Add($"lane {lane}: OK - {ackedSequences.Count} acked messages, all present exactly once " +
                    $"in {seenSequences.Count}-message history");
            }
        }
        catch (Exception ex)
        {
            reconciliation.Add($"lane {lane}: reconciliation failed: {ex.Message}");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    await operatorConnection.StopAsync();

    using (var writer = new StreamWriter(outputPath, append: false))
    {
        writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
        foreach (var r in acks)
        {
            writer.WriteLine($"ack,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)},{r.Phase}");
        }
        foreach (var (lane, at) in reconnectEvents)
        {
            writer.WriteLine($"reconnected,,{at:O},,lane-{lane}");
        }
        foreach (var e in errors)
        {
            writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
        }
    }
    File.AppendAllLines(markerPath, reconnectEvents.Select(r => $"reconnect_lane_{r.Lane}_utc={r.At:O}"));
    File.AppendAllLines(markerPath, reconciliation.Select(r => $"reconciliation: {r}"));

    Console.WriteLine();
    Console.WriteLine("=== send -> ack (outside the outage window) ===");
    ReportPercentiles("steady", acks.Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine($"successful sends: {acks.Count}, failed sends (includes outage window): {errors.Count}");
    Console.WriteLine($"reconnect events observed: {reconnectEvents.Count} across {reconnectEvents.Select(r => r.Lane).Distinct().Count()} lanes");
    Console.WriteLine("=== reconciliation (zero acknowledged-but-lost check) ===");
    foreach (var line in reconciliation) Console.WriteLine($"  {line}");
}

// ============================================================================================
// Scenario 6: assignment contention - create a reduced-depth waiting queue by starting many
// visitor conversations WITHOUT manually assigning them (unlike every other scenario here,
// which uses OperatorHub.JoinConversationAsync - a manual pick that does not consult
// IOperatorCapacity at all, confirmed by reading AssignConversationHandler). This scenario
// leaves each conversation in Waiting state and lets Ago.Chat.Worker's periodic
// ConversationAssignmentJob (SkipLockedAssignmentClaimer, 2s tick / 20-per-site batch by
// default) claim and assign them through the real capacity-checked path
// (OperatorCapacityStore.TryClaimAsync) - exactly what concurrency.md names as the thing this
// item needs to exercise. A background drain loop closes assigned conversations periodically to
// free operator capacity and keep the queue genuinely non-empty for the whole measurement
// window, instead of everything draining once and going idle.
// ============================================================================================
async Task RunAssignmentContentionAsync()
{
    var queueDepth = IntEnv("LOADDRIVER_QUEUE_DEPTH", 150);
    var createConcurrency = IntEnv("LOADDRIVER_CREATE_CONCURRENCY", 25);
    var drainIntervalMs = IntEnv("LOADDRIVER_DRAIN_INTERVAL_MS", 3000);
    var drainBatch = IntEnv("LOADDRIVER_DRAIN_BATCH", 8);
    var timeoutSeconds = IntEnv("LOADDRIVER_TIMEOUT_SECONDS", 240);

    Console.WriteLine($"[assignment-contention] queueDepth={queueDepth} createConcurrency={createConcurrency} " +
        $"drainInterval={drainIntervalMs}ms drainBatch={drainBatch} timeout={timeoutSeconds}s");

    var operatorToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-operator", "demo-operator-password");
    var adminToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-admin", "demo-admin-password");
    var tokensByOperatorId = new Dictionary<Guid, string>
    {
        // Fixed seed ids from deploy/seed/create-demo-tenant.sh - stable across runs.
        [Guid.Parse("00000000-0000-0000-0000-000000000002")] = operatorToken, // demo-operator
        [Guid.Parse("00000000-0000-0000-0000-000000000006")] = adminToken,    // demo-admin
    };

    var runStart = DateTimeOffset.UtcNow;
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\nqueue_depth={queueDepth}\n");

    var created = new ConcurrentDictionary<Guid, DateTimeOffset>();  // conversationId -> created-at
    var assignedLatencies = new ConcurrentQueue<(Guid ConversationId, TimeSpan Latency)>();
    var assignedButNotClosed = new ConcurrentQueue<(Guid ConversationId, Guid OperatorId)>();
    var closedCount = 0;
    var errors = new ConcurrentQueue<string>();
    var visitorConnections = new ConcurrentBag<HubConnection>();

    var gate = new SemaphoreSlim(createConcurrency);
    async Task CreateOneAsync(int i)
    {
        await gate.WaitAsync();
        try
        {
            // Captured before the connection even opens, deliberately - see StartVisitorAsync's
            // own remarks on `beforeJoin`. The handler below closes over this value directly
            // instead of looking it up in `created` by conversation id after the fact, so there is
            // no window in which the assignment event could arrive before this scenario knows the
            // conversation's own start time.
            var createdAt = DateTimeOffset.UtcNow;
            var visitor = await StartVisitorAsync(apiVisitorBase, beforeJoin: connection =>
            {
                connection.On<ConversationAssignedDto>("ConversationAssigned", dto =>
                {
                    var assignedAt = DateTimeOffset.UtcNow;
                    assignedLatencies.Enqueue((dto.ConversationId, assignedAt - createdAt));
                    assignedButNotClosed.Enqueue((dto.ConversationId, dto.OperatorId));
                });
            });
            created[visitor.ConversationId] = createdAt;
            visitorConnections.Add(visitor.Connection);
        }
        catch (Exception ex)
        {
            errors.Enqueue($"create {i} failed: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    Console.WriteLine($"[assignment-contention] creating {queueDepth} waiting conversations (concurrency={createConcurrency})");
    await Task.WhenAll(Enumerable.Range(0, queueDepth).Select(CreateOneAsync));
    Console.WriteLine($"[assignment-contention] all {created.Count} created, waiting for the assignment job to drain the queue");

    // Drain loop: periodically close a batch of currently-assigned-but-not-yet-closed
    // conversations to free operator capacity, so the assignment job has room to claim more of
    // the still-waiting backlog. Runs until every conversation has been assigned at least once,
    // or the timeout is hit (a real miss, reported as such - not silently extended).
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
    while (assignedLatencies.Count < queueDepth && DateTimeOffset.UtcNow < deadline)
    {
        await Task.Delay(drainIntervalMs);

        var batch = new List<(Guid ConversationId, Guid OperatorId)>();
        while (batch.Count < drainBatch && assignedButNotClosed.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        foreach (var (conversationId, operatorId) in batch)
        {
            if (!tokensByOperatorId.TryGetValue(operatorId, out var token))
            {
                errors.Enqueue($"close skipped for {conversationId}: unknown operator {operatorId}");
                continue;
            }

            try
            {
                await CloseConversationAsync(http, apiOperatorBase, token, conversationId);
                Interlocked.Increment(ref closedCount);
            }
            catch (Exception ex)
            {
                errors.Enqueue($"close failed for {conversationId}: {ex.Message}");
            }
        }

        Console.WriteLine($"[assignment-contention] {assignedLatencies.Count}/{queueDepth} assigned so far, {closedCount} closed to free capacity");
    }

    var timedOut = assignedLatencies.Count < queueDepth;
    if (timedOut)
    {
        Console.WriteLine($"[assignment-contention] TIMEOUT after {timeoutSeconds}s: only {assignedLatencies.Count}/{queueDepth} ever got assigned");
    }

    foreach (var c in visitorConnections) await c.DisposeAsync();

    using (var writer = new StreamWriter(outputPath, append: false))
    {
        writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
        foreach (var (conversationId, latency) in assignedLatencies)
        {
            writer.WriteLine($"waiting-to-assigned,{conversationId},,{latency.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)},queue-non-empty");
        }
        foreach (var e in errors)
        {
            writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== waiting -> assigned latency ===");
    ReportPercentiles("queue-non-empty", assignedLatencies.Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine($"assigned: {assignedLatencies.Count}/{queueDepth}, closed to free capacity: {closedCount}, timed out: {timedOut}, errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// Scenario 7: attachment presign throughput - presign (POST .../attachments) + a real PUT of a
// tiny payload straight to MinIO via the presigned URL (bytes bypass the API, matching
// file-storage.md) + verify (POST .../confirm, which calls IFileStorage.GetMetadataAsync and
// genuinely needs the object to exist - a confirm against a URL nothing was PUT to would fail,
// so this is a real presign+upload+verify cycle, not a presign-only stub).
// ============================================================================================
async Task RunAttachmentPresignAsync()
{
    var workers = IntEnv("LOADDRIVER_PRESIGN_WORKERS", 2);
    var intervalMs = IntEnv("LOADDRIVER_PRESIGN_INTERVAL_MS", 1300);
    var warmupSeconds = IntEnv("LOADDRIVER_WARMUP_SECONDS", 15);
    var measureSeconds = IntEnv("LOADDRIVER_MEASURE_SECONDS", 90);

    Console.WriteLine($"[attachment-presign] workers={workers} interval={intervalMs}ms " +
        $"target-rate~={1000.0 / intervalMs * workers:F2}/s warmup={warmupSeconds}s measure={measureSeconds}s");

    var visitor = await StartVisitorAsync(apiVisitorBase);
    var conversationId = visitor.ConversationId;

    var runStart = DateTimeOffset.UtcNow;
    var measureStart = runStart.AddSeconds(warmupSeconds);
    var runEnd = measureStart.AddSeconds(measureSeconds);
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\nmeasure_start_utc={measureStart:O}\nrun_end_utc={runEnd:O}\n");

    string Phase(DateTimeOffset now) => now < measureStart ? "warmup" : "measured";

    var presignLatencies = new ConcurrentQueue<(double Ms, string Phase)>();
    var confirmLatencies = new ConcurrentQueue<(double Ms, string Phase)>();
    var totalLatencies = new ConcurrentQueue<(double Ms, string Phase)>();
    var errors = new ConcurrentQueue<string>();
    // AttachmentOptions.AllowedContentTypes is a fixed server-side allow-list (image/png, image/jpeg,
    // image/gif, image/webp, application/pdf) - "text/plain" is rejected with a 400 before anything
    // else runs (found live running this scenario's own smoke test). The bytes below are not a real
    // PNG - MinIO and ConfirmAttachmentHandler.GetMetadataAsync only care that an object of the
    // declared size exists at the object key, never that its bytes decode as the declared type.
    var payload = "load-test-attachment-payload"u8.ToArray();

    async Task WorkerAsync(int worker)
    {
        // Each worker needs its own bearer token attached per-request; the visitor token from
        // StartVisitorAsync's session is shared read-only across workers (attachments are scoped
        // to the conversation + visitor, not to a single HTTP call).
        while (DateTimeOffset.UtcNow < runEnd)
        {
            var phase = Phase(DateTimeOffset.UtcNow);
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var presignSw = System.Diagnostics.Stopwatch.StartNew();
                using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{apiVisitorBase}/api/v1/conversations/{conversationId}/attachments");
                createReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", visitor.Token);
                createReq.Content = JsonContent.Create(new { contentType = "image/png", sizeBytes = (long)payload.Length });
                var createRes = await http.SendAsync(createReq);
                createRes.EnsureSuccessStatusCode();
                var created = await createRes.Content.ReadFromJsonAsync<JsonElement>();
                presignSw.Stop();
                presignLatencies.Enqueue((presignSw.Elapsed.TotalMilliseconds, phase));

                var attachmentId = created.GetProperty("attachmentId").GetGuid();
                var uploadUrl = created.GetProperty("uploadUrl").GetString()!;

                using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(payload),
                };
                putReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                var putRes = await http.SendAsync(putReq);
                putRes.EnsureSuccessStatusCode();

                var confirmSw = System.Diagnostics.Stopwatch.StartNew();
                using var confirmReq = new HttpRequestMessage(HttpMethod.Post, $"{apiVisitorBase}/api/v1/attachments/{attachmentId}/confirm");
                confirmReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", visitor.Token);
                var confirmRes = await http.SendAsync(confirmReq);
                confirmRes.EnsureSuccessStatusCode();
                confirmSw.Stop();
                confirmLatencies.Enqueue((confirmSw.Elapsed.TotalMilliseconds, phase));

                totalSw.Stop();
                totalLatencies.Enqueue((totalSw.Elapsed.TotalMilliseconds, phase));
            }
            catch (Exception ex)
            {
                errors.Enqueue($"worker {worker} at {DateTimeOffset.UtcNow:O}: {ex.Message}");
            }

            await Task.Delay(intervalMs);
        }
    }

    await Task.WhenAll(Enumerable.Range(0, workers).Select(WorkerAsync));
    await visitor.Connection.DisposeAsync();

    using (var writer = new StreamWriter(outputPath, append: false))
    {
        writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
        foreach (var (ms, phase) in presignLatencies) writer.WriteLine($"presign,,,{ms.ToString("F2", CultureInfo.InvariantCulture)},{phase}");
        foreach (var (ms, phase) in confirmLatencies) writer.WriteLine($"confirm,,,{ms.ToString("F2", CultureInfo.InvariantCulture)},{phase}");
        foreach (var (ms, phase) in totalLatencies) writer.WriteLine($"presign-plus-verify,,,{ms.ToString("F2", CultureInfo.InvariantCulture)},{phase}");
        foreach (var e in errors) writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
    }

    Console.WriteLine();
    Console.WriteLine("=== presign (POST .../attachments) ===");
    ReportPercentiles("measured", presignLatencies.Where(r => r.Phase == "measured").Select(r => r.Ms));
    Console.WriteLine("=== confirm/verify (POST .../confirm) ===");
    ReportPercentiles("measured", confirmLatencies.Where(r => r.Phase == "measured").Select(r => r.Ms));
    Console.WriteLine("=== presign+upload+verify, wall clock ===");
    ReportPercentiles("measured", totalLatencies.Where(r => r.Phase == "measured").Select(r => r.Ms));
    var measuredOps = totalLatencies.Count(r => r.Phase == "measured");
    Console.WriteLine($"measured-window throughput ~= {measuredOps / (double)measureSeconds:F2} ops/s (n={measuredOps} over {measureSeconds}s)");
    Console.WriteLine($"errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// `6-06`'s original scenario, preserved verbatim in behaviour so that item's own report stays
// reproducible from this same driver.
// ============================================================================================
async Task RunWebhookIsolationAsync()
{
    var lanes = IntEnv("LOADDRIVER_LANES", 15);
    var baselineSeconds = IntEnv("LOADDRIVER_BASELINE_SECONDS", 60);
    var hungWindowSeconds = IntEnv("LOADDRIVER_HUNG_SECONDS", 150);
    var sendIntervalMs = IntEnv("LOADDRIVER_SEND_INTERVAL_MS", 1000);
    var recycleEverySeconds = IntEnv("LOADDRIVER_RECYCLE_SECONDS", 20);
    var burstSize = IntEnv("LOADDRIVER_BULKHEAD_BURST", 25);

    Console.WriteLine($"[webhook-isolation] lanes={lanes} baseline={baselineSeconds}s hungWindow={hungWindowSeconds}s " +
        $"interval={sendIntervalMs}ms recycle={recycleEverySeconds}s");

    var operatorToken = await GetOperatorTokenAsync(http, keycloakBase, "demo-operator", "demo-operator-password");
    await using var operatorConnection = BuildHub($"{apiOperatorBase}/hubs/operator", operatorToken, autoReconnect: false);

    var runStart = DateTimeOffset.UtcNow;
    var hungStart = runStart.AddSeconds(baselineSeconds);
    var runEnd = hungStart.AddSeconds(hungWindowSeconds);

    string CurrentPhase() => DateTimeOffset.UtcNow < hungStart ? "baseline" : "hung-crm";

    var pending = new ConcurrentDictionary<Guid, DateTimeOffset>();
    var records = new ConcurrentQueue<Record>();

    operatorConnection.On<MessageDto>("MessageReceived", dto =>
    {
        var receivedAt = DateTimeOffset.UtcNow;
        if (dto.ClientMessageId is { } id && pending.TryRemove(id, out var sentAt))
        {
            records.Enqueue(new Record(id, sentAt, receivedAt - sentAt, CurrentPhase()));
        }
    });

    await operatorConnection.StartAsync();
    File.WriteAllText(markerPath, $"run_start_utc={runStart:O}\nhung_start_utc={hungStart:O}\nrun_end_utc={runEnd:O}\n");
    Console.WriteLine($"[webhook-isolation] run_start={runStart:O} hung_start={hungStart:O} run_end={runEnd:O}");
    Console.WriteLine("[webhook-isolation] >>> start Ago.Chat.WebhookDispatchRunner + hung Ago.Chat.FakeCrm at hung_start_utc above <<<");

    var ackLatencies = new ConcurrentQueue<AckRecord>();
    var errors = new ConcurrentQueue<string>();

    async Task RunBulkheadBurstAsync(int size)
    {
        var due = hungStart - DateTimeOffset.UtcNow;
        if (due > TimeSpan.Zero) await Task.Delay(due);

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

    async Task LaneAsync(int lane)
    {
        var visitorConnection = await StartVisitorAsync(apiVisitorBase);
        var conversationId = visitorConnection.ConversationId;
        await AssignToOperatorAsync(operatorConnection, conversationId);
        Console.WriteLine($"[lane {lane}] conversation {conversationId} assigned");

        var nextRecycle = DateTimeOffset.UtcNow.AddSeconds(recycleEverySeconds + lane % 5);

        while (DateTimeOffset.UtcNow < runEnd)
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

            if (DateTimeOffset.UtcNow >= nextRecycle && DateTimeOffset.UtcNow < runEnd.AddSeconds(-5))
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

    var burstTask = RunBulkheadBurstAsync(burstSize);
    var laneTasks = Enumerable.Range(0, lanes).Select(LaneAsync).ToArray();
    await Task.WhenAll([burstTask, .. laneTasks]);

    await operatorConnection.StopAsync();

    WriteLatencyCsv(outputPath, ackLatencies, records, errors);

    Console.WriteLine();
    Console.WriteLine("=== send -> ack ===");
    ReportPercentiles("baseline", ackLatencies.Where(r => r.Phase == "baseline").Select(r => r.Latency.TotalMilliseconds));
    ReportPercentiles("hung-crm", ackLatencies.Where(r => r.Phase == "hung-crm").Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine("=== send -> delivered (operator, cross-node) ===");
    ReportPercentiles("baseline", records.Where(r => r.Phase == "baseline").Select(r => r.Latency.TotalMilliseconds));
    ReportPercentiles("hung-crm", records.Where(r => r.Phase == "hung-crm").Select(r => r.Latency.TotalMilliseconds));
    Console.WriteLine($"errors: {errors.Count}");
    foreach (var e in errors.Take(20)) Console.WriteLine($"  {e}");
}

// ============================================================================================
// Shared helpers
// ============================================================================================

int IntEnv(string name, int defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return raw is null ? defaultValue : int.Parse(raw, CultureInfo.InvariantCulture);
}

double DoubleEnv(string name, double defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return raw is null ? defaultValue : double.Parse(raw, CultureInfo.InvariantCulture);
}

void WriteLatencyCsv(string path, IEnumerable<AckRecord> acks, IEnumerable<Record> delivered, IEnumerable<string> errorList)
{
    using var writer = new StreamWriter(path, append: false);
    writer.WriteLine("kind,client_message_id,sent_utc,latency_ms,phase");
    foreach (var r in acks)
    {
        writer.WriteLine($"ack,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)},{r.Phase}");
    }
    foreach (var r in delivered)
    {
        writer.WriteLine($"delivered,{r.ClientMessageId},{r.SentAt:O},{r.Latency.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)},{r.Phase}");
    }
    foreach (var e in errorList)
    {
        writer.WriteLine($"error,,,,{e.Replace(',', ';')}");
    }
    Console.WriteLine($"[driver] wrote {path}");
}

void ReportPercentiles(string label, IEnumerable<double> values)
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

async Task<string> GetOperatorTokenAsync(HttpClient client, string keycloak, string username, string password)
{
    // `7-05`: a pre-minted token overrides the direct Keycloak call entirely. The k8s overlay's
    // `Ago.Chat.Api` validates tokens against the in-cluster issuer `http://keycloak:8080/realms/ago-chat`
    // (`Auth__Keycloak__Authority`) - Keycloak stamps a token's `iss` claim from however *it* was reached,
    // not from the audience that will later present it, so a token minted by calling Keycloak through a
    // `kubectl port-forward` (or any other host-reachable address) carries the wrong issuer and every
    // k8s-cluster hub connection rejects it with a real, reproducible 401 (confirmed live - `iss` came
    // back `http://127.0.0.1:<forwarded-port>/realms/ago-chat`, not the in-cluster name). Minting from
    // *inside* the cluster's own network (a throwaway `kubectl run` pod hitting the `keycloak` Service by
    // name) gets the matching issuer; this override lets that token be handed to a driver process that
    // otherwise talks to the cluster only through the public Gateway address, exactly as a real client
    // would.
    var presetToken = Environment.GetEnvironmentVariable("LOADDRIVER_OPERATOR_TOKEN");
    if (!string.IsNullOrEmpty(presetToken))
    {
        return presetToken;
    }

    var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "password",
        ["client_id"] = "ago-console",
        ["username"] = username,
        ["password"] = password,
    });
    var res = await client.PostAsync($"{keycloak}/realms/ago-chat/protocol/openid-connect/token", form);
    res.EnsureSuccessStatusCode();
    var body = await res.Content.ReadFromJsonAsync<JsonElement>();
    return body.GetProperty("access_token").GetString()!;
}

async Task<VisitorSession> StartVisitorAsync(string apiBase, bool autoReconnect = false, Action<HubConnection>? beforeJoin = null)
{
    var res = await http.PostAsJsonAsync($"{apiBase}/api/v1/visitor-sessions", new { publicKey = PublicKey });
    res.EnsureSuccessStatusCode();
    var body = await res.Content.ReadFromJsonAsync<JsonElement>();
    var token = body.GetProperty("token").GetString()!;

    var connection = BuildHub($"{apiBase}/hubs/visitor", token, autoReconnect);
    await connection.StartAsync();
    // `beforeJoin` runs after the connection is live but before `JoinAsync` creates the
    // conversation - callers that need to observe a server push keyed to this conversation (e.g.
    // assignment-contention's "ConversationAssigned") MUST register their `.On<T>(...)` handler
    // here, not after this method returns. Registering it after `JoinAsync` races the automatic
    // assignment job: on a mostly-empty queue, the Worker's next tick can claim and push
    // "ConversationAssigned" within milliseconds of the conversation existing, before a
    // post-return `.On(...)` call would have executed - a message for a method with no handler
    // registered yet is silently dropped by the SignalR client, not buffered. Found live running
    // this scenario's own real run: only 5/150 assignment events were observed even though the
    // operators' active_chats correctly showed full capacity consumed, because most assignments
    // raced ahead of the handler registration that used to happen after this method returned.
    beforeJoin?.Invoke(connection);
    var join = await connection.InvokeAsync<VisitorJoinResult>("JoinAsync", (int?)null);
    return new VisitorSession(connection, join.ConversationId, token);
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

HubConnection BuildHub(string url, string token, bool autoReconnect) =>
    autoReconnect
        ? new HubConnectionBuilder()
            .WithUrl($"{url}?access_token={token}", options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            })
            .Build()
        : new HubConnectionBuilder()
            .WithUrl($"{url}?access_token={token}", options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();

internal sealed record VisitorSession(HubConnection Connection, Guid ConversationId, string Token);

internal sealed record MessageDto(
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTimeOffset CreatedAt,
    Guid? AttachmentId, Guid? ClientMessageId, Guid? ConversationId);

internal sealed record VisitorJoinResult(Guid ConversationId, bool IsNew, IReadOnlyList<MessageDto> History);

internal sealed record HistoryPage(IReadOnlyList<MessageDto> Messages, int? NextBeforeSequence);

internal sealed record Record(Guid ClientMessageId, DateTimeOffset SentAt, TimeSpan Latency, string Phase);

internal sealed record AckRecord(Guid ClientMessageId, DateTimeOffset SentAt, TimeSpan Latency, string Phase);

internal sealed record ConversationAssignedDto(Guid ConversationId, Guid OperatorId, DateTimeOffset AssignedAt);
