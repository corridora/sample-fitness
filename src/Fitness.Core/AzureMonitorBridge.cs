using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace Fitness.Core;
public sealed class AzureMonitorBridge(Settings settings, IStore store, Telemetry telemetry)
{
    public static IEnumerable<(string Name, double Value, string Resource)> ConvertMetrics(JsonElement body)
    {
        if (!body.TryGetProperty("value", out var values)) yield break;
        foreach (var metric in values.EnumerateArray())
        {
            var name = metric.GetProperty("name").GetProperty("value").GetString() ?? "unknown";
            var resource = metric.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            foreach (var series in metric.GetProperty("timeseries").EnumerateArray())
                foreach (var point in series.GetProperty("data").EnumerateArray())
                    foreach (var field in new[] { "total", "average", "maximum" })
                        if (point.TryGetProperty(field, out var value) && value.TryGetDouble(out var number)) { yield return (name, number, resource); break; }
        }
    }
    public static IEnumerable<(string Resource, string Category, string Message)> ConvertLogs(JsonElement body)
    {
        if (!body.TryGetProperty("records", out var records)) yield break;
        foreach (var record in records.EnumerateArray())
        {
            var resource = record.TryGetProperty("resourceId", out var id) ? id.GetString() ?? "" : "";
            var category = record.TryGetProperty("category", out var cat) ? cat.GetString() ?? "unknown" : "unknown";
            // Export safe metadata only; original provider payload may contain account or request details.
            var operation = record.TryGetProperty("operationName", out var op) ? op.GetString() ?? "" : "";
            yield return (resource, category, operation);
        }
    }
    public async Task<bool> ForwardDiagnostics(JsonElement body, string checkpointId, string etag)
    {
        if (settings.IngestKey == "") return false;
        var failuresBefore = Telemetry.ExportFailures;
        foreach (var (resource, category, message) in ConvertLogs(body))
        {
            using var scope = telemetry.Log.BeginScope(new Dictionary<string, object> { ["azure.resource_id"] = resource, ["azure.category"] = category, ["event.name"] = "azure.platform.log", ["telemetry.adapter"] = "azure-monitor" });
            telemetry.Log.LogInformation("Azure platform operation {Operation}", message);
        }
        // A drained SDK buffer alone is insufficient: exporter HTTP failures must keep the source replayable.
        var flushed = telemetry.Flush();
        if (!flushed || Telemetry.ExportFailures != failuresBefore) return false;
        await store.Put("bridge", checkpointId, etag);
        return true;
    }
    public async Task Poll()
    {
        using var activity = Telemetry.Source.StartActivity("azure.monitor.bridge", ActivityKind.Internal);
        var credential = new DefaultAzureCredential();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]));
        http.DefaultRequestHeaders.Authorization = new("Bearer", access.Token);
        var end = DateTimeOffset.UtcNow; var start = end.AddMinutes(-15);
        var uri = $"https://management.azure.com{settings.ApiResourceId}/providers/microsoft.insights/metrics?api-version=2023-10-01&metricnames=Requests&interval=PT15M&aggregation=Total&timespan={Uri.EscapeDataString(start.ToString("O") + "/" + end.ToString("O"))}";
        using var result = await http.GetAsync(uri); result.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        foreach (var (name, value, resource) in ConvertMetrics(json.RootElement)) Telemetry.ProviderMetric.Record(value, new KeyValuePair<string, object?>("azure.metric.name", name), new KeyValuePair<string, object?>("azure.resource_id", resource), new KeyValuePair<string, object?>("telemetry.adapter", "azure-monitor"));
        var blobs = new BlobServiceClient(new Uri($"https://{settings.DiagnosticStorage}.blob.core.windows.net"), credential);
        var count = 0;
        await foreach (var container in blobs.GetBlobContainersAsync(prefix: "insights-logs-"))
        {
            var client = blobs.GetBlobContainerClient(container.Name);
            await foreach (var item in client.GetBlobsAsync())
            {
                if (count >= 20) break;
                if (item.Properties.LastModified < end.AddMinutes(-30) || item.Properties.ContentLength > 2_000_000) continue;
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(container.Name + "/" + item.Name))).ToLowerInvariant();
                var etag = item.Properties.ETag?.ToString() ?? "";
                if (await store.Get<string>("bridge", key) == etag) continue;
                using var data = JsonDocument.Parse((await client.GetBlobClient(item.Name).DownloadContentAsync()).Value.Content.ToString());
                await ForwardDiagnostics(data.RootElement, key, etag); count++;
            }
        }
        activity?.Stop(); telemetry.Flush();
    }
}
