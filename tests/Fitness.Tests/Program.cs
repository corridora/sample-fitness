using System.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Fitness.Core;

var passed = 0;
void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); passed++; Console.WriteLine("PASS " + reason); }
var folder = Path.Combine(Path.GetTempPath(), "fitness-tests-" + Guid.NewGuid());
var settings = new Settings { IngestKey = "", RumKey = "", DailyStart = "14:00", WindowSeconds = 600, LocalPath = folder };
using var telemetry = new Telemetry(settings, "sample-fitness-test");
var store = new LocalStore(folder); var scenario = new Scenario(store, settings, telemetry);
try
{
    Assert((await scenario.Effective(DateTimeOffset.Parse("2026-10-11T14:05:00Z"))).Phase == "regression", "UTC daily window opens");
    Assert((await scenario.Effective(DateTimeOffset.Parse("2026-10-11T14:10:00Z"))).Phase == "healthy", "UTC daily window ends without timer");
    var rollover = new Scenario(store, settings with { DailyStart = "23:55" }, telemetry);
    var midnight = await rollover.Effective(DateTimeOffset.Parse("2026-10-12T00:01:00Z"));
    Assert(midnight.Phase == "regression" && midnight.Release == "serializer-v2-20261011", "daily regression carries previous start across UTC midnight");
    Assert((await rollover.Effective(DateTimeOffset.Parse("2026-10-12T00:05:00Z"))).Phase == "healthy", "cross-midnight daily window expires precisely");
    var threw = false; try { await scenario.Set("regression", 601); } catch (ArgumentException) { threw = true; } Assert(threw, "invalid unbounded control rejected");
    await scenario.Set("regression", 1);
    Assert((await scenario.Effective()).Phase == "regression", "override applies immediately");
    await store.Put("control", "scenario", new Control("regression", DateTimeOffset.Parse("2026-10-11T14:30:00Z"), "expired"));
    Assert((await scenario.Effective(DateTimeOffset.Parse("2026-10-11T15:00:00Z"))).Phase == "healthy", "expired override cannot persist fault");
    await scenario.Set("regression", 120);
    var processor = new SummaryProcessor(store, scenario, telemetry);
    var parent = "00-11111111111111111111111111111111-2222222222222222-01";
    var bad = new SummaryMessage("seeded", "demo-workout", parent, "", DateTimeOffset.UtcNow.AddSeconds(120), "test-bad");
    for (var attempt = 1; attempt <= 3; attempt++) { try { await processor.Process(bad, attempt); } catch (JsonException) { } }
    Assert((await store.Get<Job>("job", "seeded"))?.Status == "poison", "seeded serializer reaches poison after 3 attempts");
    await processor.Poison(bad); Assert(await store.Get<SummaryMessage>("poison", "seeded") != null, "poison envelope retained for recovery evidence");
    await processor.Process(bad with { JobId = "healthy", WorkoutId = "healthy-workout" }, 1);
    Assert((await store.Get<Job>("job", "healthy"))?.Status == "complete", "healthy control is unaffected during regression");
    await processor.Process(bad with { JobId = "expired-message", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, 1);
    Assert((await store.Get<Job>("job", "expired-message"))?.Status == "complete", "expired queue envelope cannot inject a fault");
    await scenario.Set("recover", 120); await processor.Process(bad with { JobId = "recovered" }, 1);
    Assert((await store.Get<Job>("job", "recovered"))?.Status == "complete", "recovery succeeds on fresh replay");
    using var metric = JsonDocument.Parse("{\"value\":[{\"id\":\"resource\",\"name\":{\"value\":\"Requests\"},\"timeseries\":[{\"data\":[{\"total\":12}]}]}]}");
    var converted = AzureMonitorBridge.ConvertMetrics(metric.RootElement).Single(); Assert(converted.Name == "Requests" && converted.Value == 12, "native Azure metrics convert preserving value/name");
    using var log = JsonDocument.Parse("{\"records\":[{\"resourceId\":\"resource\",\"category\":\"AuditEvent\",\"operationName\":\"SecretGet\",\"properties\":{\"secret\":\"must-not-export\"}}]}");
    var record = AzureMonitorBridge.ConvertLogs(log.RootElement).Single(); Assert(record.Message == "SecretGet" && !record.Message.Contains("must-not-export"), "native provider logs export safe metadata only");
    var listenerPort = new TcpListener(IPAddress.Loopback, 0); listenerPort.Start(); var port = ((IPEndPoint)listenerPort.LocalEndpoint).Port; listenerPort.Stop();
    using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    var responseStatus = 503;
    var serving = Task.Run(async () => {
        try { while (listener.IsListening) { var request = await listener.GetContextAsync(); await request.Request.InputStream.CopyToAsync(Stream.Null); request.Response.StatusCode = Volatile.Read(ref responseStatus); request.Response.ContentType = "application/x-protobuf"; request.Response.Close(); } }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    });
    using (var exportTelemetry = new Telemetry(settings with { IngestKey = "mock-key", Endpoint = $"http://127.0.0.1:{port}" }, "sample-fitness-adapter-test"))
    {
        var bridge = new AzureMonitorBridge(settings with { IngestKey = "mock-key" }, store, exportTelemetry);
        Assert(!await bridge.ForwardDiagnostics(log.RootElement, "checkpoint-test", "etag-one"), "rejected OTLP export leaves diagnostic blob replayable");
        Assert(await store.Get<string>("bridge", "checkpoint-test") == null, "failed native export does not advance source checkpoint");
        Volatile.Write(ref responseStatus, 200);
        Assert(await bridge.ForwardDiagnostics(log.RootElement, "checkpoint-test", "etag-one"), "acknowledged SDK log flush permits source checkpoint");
        Assert(await store.Get<string>("bridge", "checkpoint-test") == "etag-one", "successful native export checkpoints ETag");
    }
    listener.Stop(); await serving;
    Console.WriteLine($"{passed} core assertions passed");
}
finally { Directory.Delete(folder, true); }
