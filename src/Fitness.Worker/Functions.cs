using System.Text.Json;
using System.Net.Http.Json;
using Azure.Storage.Queues.Models;
using Fitness.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fitness.Worker;
public sealed class Functions(SummaryProcessor processor, AzureMonitorBridge bridge, Settings settings, Telemetry telemetry)
{
    [Function("WeeklySummary")]
    public async Task Summary([QueueTrigger("weekly-summary", Connection = "AzureWebJobsStorage")] QueueMessage message)
    {
        var item = JsonSerializer.Deserialize<SummaryMessage>(message.MessageText) ?? throw new JsonException("Summary envelope required");
        await processor.Process(item, checked((int)message.DequeueCount));
    }
    [Function("SummaryPoison")]
    public async Task Poison([QueueTrigger("weekly-summary-poison", Connection = "AzureWebJobsStorage")] QueueMessage message)
    {
        var item = JsonSerializer.Deserialize<SummaryMessage>(message.MessageText) ?? throw new JsonException("Summary envelope required");
        await processor.Poison(item);
    }
    [Function("ProviderBridge")]
    public async Task Provider([TimerTrigger("0 */15 * * * *")] TimerInfo timer)
    { if (settings.Forwarding) await bridge.Poll(); }
    [Function("DailyIncident")]
    public async Task Daily([TimerTrigger("%DEMO_DAILY_TIMER%")] TimerInfo timer)
    { if (Settings.Get("ENABLE_SYNTHETICS", "false") == "true") await Dispatch(false); }
    [Function("DailyWatchdog")]
    public async Task Watchdog([TimerTrigger("%DEMO_WATCHDOG_TIMER%")] TimerInfo timer)
    { if (Settings.Get("ENABLE_SYNTHETICS", "false") == "true") await Dispatch(true); }
    private async Task Dispatch(bool watchdog)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        client.BaseAddress = new Uri(settings.PublicUrl);
        client.DefaultRequestHeaders.Authorization = new("Bearer", settings.ControlToken);
        if (watchdog)
        {
            var result = await client.GetAsync("/api/demo/heartbeat");
            if (result.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("day", out var day) && day.GetString() == DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")) return;
            }
            var rollout = await client.PostAsJsonAsync("/api/demo/scenario", new { phase = "regression", duration_seconds = 120 }); rollout.EnsureSuccessStatusCode();
            telemetry.Event("deployment", "regression", "watchdog-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd"), "Browser heartbeat missing; replaying seeded backend symptom. Browser runner must still complete.");
        }
        if (!watchdog) { var rollout = await client.PostAsJsonAsync("/api/demo/scenario", new { phase = "regression", duration_seconds = Math.Clamp(settings.WindowSeconds, 30, 120) }); rollout.EnsureSuccessStatusCode(); }
        var seeded = await client.PostAsJsonAsync("/api/summaries", new { workout_id = "demo-workout" }); seeded.EnsureSuccessStatusCode();
        var control = await client.PostAsJsonAsync("/api/summaries", new { workout_id = "healthy-workout" }); control.EnsureSuccessStatusCode();
        telemetry.Log.LogInformation("Daily seeded summary and healthy control enqueued; browser heartbeat is recorded only by Playwright");
        telemetry.Flush();
    }
}
