using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fitness.Core;
using Microsoft.Extensions.FileProviders;

var settings = new Settings(); settings.Validate();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Settings.Get("PORT", "4104")}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 65536);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new Telemetry(settings, Settings.Get("OTEL_SERVICE_NAME", "sample-fitness-api")));
builder.Services.AddSingleton<IStore>(_ => settings.Mode == "cloud" ? new AzureStore(settings) : new LocalStore(settings.LocalPath));
builder.Services.AddSingleton<Scenario>();
if (settings.Mode == "cloud") builder.Services.AddSingleton<ISummaryQueue, AzureSummaryQueue>();
else { builder.Services.AddSingleton<LocalSummaryQueue>(); builder.Services.AddSingleton<ISummaryQueue>(s => s.GetRequiredService<LocalSummaryQueue>()); builder.Services.AddHostedService<LocalQueueWorker>(); }
builder.Services.AddSingleton<SummaryService>(); builder.Services.AddSingleton<SummaryProcessor>();
var app = builder.Build();
var telemetry = app.Services.GetRequiredService<Telemetry>();
app.Use(async (http, next) => {
    var dynamic = http.Request.Path.StartsWithSegments("/api") || http.Request.Path == "/healthz";
    if (!dynamic) { await next(); return; }
    http.Response.Headers.CacheControl = "no-store";
    ActivityContext.TryParse(http.Request.Headers.TraceParent.ToString(), http.Request.Headers.TraceState.ToString(), isRemote: true, out var parent);
    var frameworkActivity = Activity.Current;
    Activity.Current = null; // The manually instrumented SERVER span owns the incoming W3C context.
    using var span = Telemetry.Source.StartActivity($"{http.Request.Method} {http.Request.Path}", ActivityKind.Server, parent);
    span?.SetTag("http.request.method", http.Request.Method); span?.SetTag("url.path", http.Request.Path.ToString());
    var state = await http.RequestServices.GetRequiredService<Scenario>().Effective();
    span?.SetTag("demo.scenario", "serializer-poison-summary"); span?.SetTag("demo.phase", state.Phase); span?.SetTag("demo.release", state.Release);
    try { await next(); span?.SetTag("http.response.status_code", http.Response.StatusCode); if (http.Response.StatusCode >= 500) span?.SetStatus(ActivityStatusCode.Error); }
    catch (Exception e) { span?.SetStatus(ActivityStatusCode.Error, e.GetType().Name); telemetry.Log.LogError("API failed: {ErrorType}", e.GetType().Name); if (!http.Response.HasStarted) { http.Response.StatusCode = 500; await http.Response.WriteAsJsonAsync(new { error = "Request failed; inspect correlated backend trace" }); } }
    finally { span?.Stop(); telemetry.Flush(); Activity.Current = frameworkActivity; }
});
app.MapGet("/healthz", () => Results.Json(new { status = "ok", exporter_failures = Telemetry.ExportFailures, service_version = settings.Version }));
app.MapGet("/api/trace-check", () => Results.Json(new { trace_id = Activity.Current?.TraceId.ToHexString(), span_id = Activity.Current?.SpanId.ToHexString() }));
app.MapGet("/api/demo/status", async (Scenario scenario) => { var state = await scenario.Effective(); return Results.Json(new { phase = state.Phase, expires_at = state.ExpiresAt, release = state.Release, scheduled = state.Scheduled, service_version = settings.Version, scenario = "serializer-poison-summary" }); });
app.MapPost("/api/demo/scenario", async (HttpContext http, Scenario scenario) => {
    if (!Authorized(http, settings.ControlToken)) return Results.Unauthorized();
    ScenarioRequest? body;
    try { body = await http.Request.ReadFromJsonAsync<ScenarioRequest>(); } catch (JsonException) { return Results.BadRequest(new { error = "valid JSON required" }); }
    if (body == null) return Results.BadRequest();
    try { return Results.Json(await scenario.Set(body.phase, body.duration_seconds)); } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});
Workout[] seed = [new("demo-workout", "Tempo run", 32, "Today", true), new("healthy-workout", "Recovery walk", 20, "Yesterday", true), new("mobility-workout", "Morning mobility", 15, "Monday", true)];
app.MapGet("/api/workouts", async (IStore store) => {
    var workouts = await store.Get<Workout[]>("seed", "workouts");
    if (workouts == null) { workouts = seed; await store.Put("seed", "workouts", workouts); }
    Telemetry.Workouts.Add(1); telemetry.Log.LogInformation("Loaded {Count} fictional workouts", workouts.Length);
    return Results.Json(new { workouts, fictional = true });
});
app.MapPost("/api/summaries", async (HttpContext http, SummaryService summaries) => {
    SummaryRequest? request; try { request = await http.Request.ReadFromJsonAsync<SummaryRequest>(); } catch (JsonException) { return Results.BadRequest(); }
    if (request == null || !seed.Any(w => w.Id == request.workout_id)) return Results.BadRequest(new { error = "Choose a seeded workout" });
    return Results.Accepted(value: await summaries.Enqueue(request.workout_id));
});
app.MapGet("/api/summaries/{id}", async (string id, IStore store) => {
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(); var job = await store.Get<Job>("job", id); return job == null ? Results.NotFound() : Results.Json(job);
});
app.MapGet("/api/recommendations", async () => {
    using var clientSpan = Telemetry.Source.StartActivity("recommender GET", ActivityKind.Client);
    if (settings.Recommender == "") { clientSpan?.SetTag("dependency.fixture", true); return Results.Json(new { items = new[] { new { id = "demo-product", score = 0.9 } }, model_revision = "fixture-v1", suggestion = "A gentle recovery day" }); }
    var uri = new Uri(new Uri(settings.Recommender.TrimEnd('/') + "/"), "api/recommendations?product_id=demo-product");
    using var request = new HttpRequestMessage(HttpMethod.Get, uri); if (Activity.Current?.Id is { } traceparent) request.Headers.TryAddWithoutValidation("traceparent", traceparent); if (Activity.Current?.TraceStateString is { } tracestate) request.Headers.TryAddWithoutValidation("tracestate", tracestate);
    if (settings.DownstreamToken != "") request.Headers.Authorization = new("Bearer", settings.DownstreamToken);
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode(); var bytes = await ReadBounded(response.Content, timeout.Token); using var json = JsonDocument.Parse(bytes); return Results.Json(json.RootElement.Clone()); }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { clientSpan?.SetStatus(ActivityStatusCode.Error, e.GetType().Name); return Results.Json(new { items = new[] { new { id = "demo-product", score = 0.9 } }, model_revision = "fallback-v1", fallback = true }); }
});
app.MapGet("/api/export", async (IStore store) => { var data = await store.Export("fictional-workouts.json", JsonSerializer.SerializeToUtf8Bytes(seed)); return Results.File(data, "application/json", "fictional-workouts.json"); });
app.MapPost("/api/demo/heartbeat", async (HttpContext http, IStore store) => { if (!Authorized(http, settings.ControlToken)) return Results.Unauthorized(); var result = await http.Request.ReadFromJsonAsync<JsonElement>(); if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return Results.BadRequest(); await store.Put("control", "heartbeat", new { day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), at = DateTimeOffset.UtcNow, result }); return Results.Ok(); });
app.MapGet("/api/demo/heartbeat", async (HttpContext http, IStore store) => { if (!Authorized(http, settings.ControlToken)) return Results.Unauthorized(); return Results.Json(await store.Get<JsonElement?>("control", "heartbeat")); });
var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(webRoot)) app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
app.MapFallback(async http => {
    if (http.Request.Path.StartsWithSegments("/api")) { http.Response.StatusCode = 404; return; }
    var file = Path.Combine(webRoot, "index.html"); if (!File.Exists(file)) { http.Response.StatusCode = 503; await http.Response.WriteAsync("Run ./setup deploy to build Angular assets first."); return; }
    Func<string, string> encode = value => WebUtility.HtmlEncode(value);
    var rumOrigin = new Uri(settings.RumScript).GetLeftPart(UriPartial.Authority);
    var ingestOrigin = new Uri(settings.Endpoint).GetLeftPart(UriPartial.Authority);
    http.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' {rumOrigin}; connect-src 'self' {ingestOrigin}; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    http.Response.Headers.CacheControl = "no-store";
    var separator = settings.RumScript.Contains('?') ? "&" : "?";
    var rum = settings.RumKey == "" ? "" : $"<script src=\"{encode(settings.RumScript + separator + "ckid=" + Uri.EscapeDataString(settings.RumKey))}\" data-endpoint=\"{encode(settings.Endpoint)}\" data-service=\"sample-fitness-web\" data-service-namespace=\"{encode(settings.Namespace)}\" data-env=\"{encode(settings.Environment)}\" data-version=\"{encode(settings.Version)}\" data-encoding=\"json\" data-trace-sample-rate=\"1\" data-long-tasks=\"true\" data-trace-origins=\"{encode(new Uri(settings.PublicUrl).GetLeftPart(UriPartial.Authority))}\"></script>";
    http.Response.ContentType = "text/html; charset=utf-8";
    await http.Response.WriteAsync((await File.ReadAllTextAsync(file)).Replace("<!--RUM-->", rum));
});
await app.RunAsync();
static async Task<byte[]> ReadBounded(HttpContent content, CancellationToken token)
{
    await using var stream = await content.ReadAsStreamAsync(token);
    using var buffer = new MemoryStream();
    var chunk = new byte[4096];
    while (true)
    {
        var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, 65537 - checked((int)buffer.Length))), token);
        if (read == 0) return buffer.ToArray();
        await buffer.WriteAsync(chunk.AsMemory(0, read), token);
        if (buffer.Length > 65536) throw new HttpRequestException("Controlled dependency response exceeds 64 KiB");
    }
}
static bool Authorized(HttpContext http, string token)
{
    if (string.IsNullOrWhiteSpace(token)) return false;
    var expected = Encoding.UTF8.GetBytes("Bearer " + token); var actual = Encoding.UTF8.GetBytes(http.Request.Headers.Authorization.ToString());
    return CryptographicOperations.FixedTimeEquals(actual, expected);
}
sealed record ScenarioRequest(string phase, int duration_seconds);
sealed record SummaryRequest(string workout_id);
sealed class LocalQueueWorker(LocalSummaryQueue queue, SummaryProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Messages.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try { await processor.Process(message, attempt); break; }
                catch (JsonException) { if (attempt == 3) await processor.Poison(message); else await Task.Delay(1000, stoppingToken); }
            }
        }
    }
}
