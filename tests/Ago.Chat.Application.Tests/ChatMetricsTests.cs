using Ago.Chat.Contracts;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Application.Tests;

/// <summary>
/// `7-02`'s Done-when: proves a real value change per instrument, read back from OpenTelemetry's own
/// in-memory reader - not merely that <see cref="ChatMetrics"/>' instruments were registered.
/// <see cref="ChatMetrics.RecordHubMethod"/> is exercised directly rather than through a real SignalR
/// hub call: `VisitorHub.SendMessageAsync`/`OperatorHub.SendMessageAsync` are two lines of
/// stopwatch/try-finally boilerplate around this exact call (see their own `7-02` comments), so this
/// is the one place the actual recording logic - which tags land on which instrument, what an error
/// outcome does - lives and is worth testing directly; standing up a real hub connection to exercise
/// the same two lines would test SignalR plumbing this item did not change, not this item's own logic.
/// </summary>
public sealed class ChatMetricsTests
{
    [Fact]
    public void RecordHubMethod_OnSuccess_RecordsDurationAndSuccessCount()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        ChatMetrics.RecordHubMethod("VisitorHub", "SendMessage", TimeSpan.FromMilliseconds(42), success: true);
        meterProvider.ForceFlush();

        var duration = exportedMetrics.Single(m => m.Name == ChatMetrics.HubMethodDurationInstrumentName);
        double durationSum = 0;
        var durationPointCount = 0;
        foreach (ref readonly var point in duration.GetMetricPoints())
        {
            durationSum += point.GetHistogramSum();
            durationPointCount++;
        }

        Assert.Equal(1, durationPointCount);
        Assert.True(durationSum > 0);

        var count = exportedMetrics.Single(m => m.Name == ChatMetrics.HubMethodCountInstrumentName);
        Assert.Equal(1, SumMatching(count, "VisitorHub", "SendMessage", "success"));
        Assert.Equal(0, SumMatching(count, "VisitorHub", "SendMessage", "error"));
    }

    [Fact]
    public void RecordHubMethod_OnFailure_RecordsAnErrorOutcome_NotSuccess()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        ChatMetrics.RecordHubMethod("OperatorHub", "SendMessage", TimeSpan.FromMilliseconds(5), success: false);
        meterProvider.ForceFlush();

        var count = exportedMetrics.Single(m => m.Name == ChatMetrics.HubMethodCountInstrumentName);
        Assert.Equal(1, SumMatching(count, "OperatorHub", "SendMessage", "error"));
        Assert.Equal(0, SumMatching(count, "OperatorHub", "SendMessage", "success"));
    }

    [Fact]
    public void RecordCapacityClaimAttempt_TracksAttemptsAndConflictsSeparately()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        ChatMetrics.RecordCapacityClaimAttempt(claimed: true);
        ChatMetrics.RecordCapacityClaimAttempt(claimed: false);
        ChatMetrics.RecordCapacityClaimAttempt(claimed: false);
        meterProvider.ForceFlush();

        var attempts = exportedMetrics.Single(m => m.Name == ChatMetrics.AssignmentCapacityClaimAttemptsInstrumentName);
        long claimedTotal = 0;
        long conflictTotal = 0;
        foreach (ref readonly var point in attempts.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key != "outcome")
                {
                    continue;
                }

                if ((string?)tag.Value == "claimed")
                {
                    claimedTotal += point.GetSumLong();
                }
                else if ((string?)tag.Value == "conflict")
                {
                    conflictTotal += point.GetSumLong();
                }
            }
        }

        Assert.Equal(1, claimedTotal);
        Assert.Equal(2, conflictTotal);

        var conflicts = exportedMetrics.Single(m => m.Name == ChatMetrics.AssignmentCapacityClaimConflictsInstrumentName);
        long conflictsOnly = 0;
        foreach (ref readonly var point in conflicts.GetMetricPoints())
        {
            conflictsOnly += point.GetSumLong();
        }

        Assert.Equal(2, conflictsOnly);
    }

    /// <summary>`26-04`: proves the three push instruments are wired the way
    /// <see cref="ChatMetrics.PushSendsInstrumentName"/>'s own remarks describe - not yet called by any
    /// production code (`26-05` is the real caller and does not exist yet), so this is the only place
    /// this item's own tag shape is exercised at all.</summary>
    [Fact]
    public void RecordPushMethods_TagEachInstrumentTheWayItsRemarksDescribe()
    {
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        ChatMetrics.RecordPushSend("message", "rustore", "delivered");
        ChatMetrics.RecordPushSuppressed("no_devices");
        ChatMetrics.RecordPushTokenRevoked("provider_unregistered");
        meterProvider.ForceFlush();

        var sends = exportedMetrics.Single(m => m.Name == ChatMetrics.PushSendsInstrumentName);
        Assert.Equal(1, SumMatchingTags(sends, ("reason", "message"), ("provider", "rustore"), ("outcome", "delivered")));

        var suppressed = exportedMetrics.Single(m => m.Name == ChatMetrics.PushSuppressedInstrumentName);
        Assert.Equal(1, SumMatchingTags(suppressed, ("reason", "no_devices")));

        var revoked = exportedMetrics.Single(m => m.Name == ChatMetrics.PushTokensRevokedInstrumentName);
        Assert.Equal(1, SumMatchingTags(revoked, ("cause", "provider_unregistered")));
    }

    private static long SumMatchingTags(Metric metric, params (string Key, string Value)[] expectedTags)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var tags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in point.Tags)
            {
                tags.Add(tag);
            }

            var matches = expectedTags.All(expected =>
                tags.Any(tag => tag.Key == expected.Key && (string?)tag.Value == expected.Value));
            if (matches)
            {
                total += point.GetSumLong();
            }
        }

        return total;
    }

    private static long SumMatching(Metric metric, string hub, string method, string outcome)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            string? pointHub = null;
            string? pointMethod = null;
            string? pointOutcome = null;
            foreach (var tag in point.Tags)
            {
                switch (tag.Key)
                {
                    case "hub":
                        pointHub = tag.Value as string;
                        break;
                    case "method":
                        pointMethod = tag.Value as string;
                        break;
                    case "outcome":
                        pointOutcome = tag.Value as string;
                        break;
                }
            }

            if (pointHub == hub && pointMethod == method && pointOutcome == outcome)
            {
                total += point.GetSumLong();
            }
        }

        return total;
    }
}
