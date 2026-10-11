using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Fitness.Core;
public sealed record Document(string id, string kind, string payload);
public interface IStore
{
    Task<T?> Get<T>(string kind, string id);
    Task Put<T>(string kind, string id, T value);
    Task<byte[]> Export(string name, byte[] body);
}
public sealed class LocalStore : IStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1);
    public LocalStore(string path) { this.path = Path.GetFullPath(path); Directory.CreateDirectory(this.path); }
    private string FileName(string kind, string id) => Path.Combine(path, $"{kind}-{id}.json");
    public async Task<T?> Get<T>(string kind, string id)
    {
        using var span = Telemetry.Source.StartActivity("local.store.read", ActivityKind.Client);
        await gate.WaitAsync(); try { var file = FileName(kind, id); return File.Exists(file) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file)) : default; } finally { gate.Release(); }
    }
    public async Task Put<T>(string kind, string id, T value)
    {
        using var span = Telemetry.Source.StartActivity("local.store.write", ActivityKind.Client);
        await gate.WaitAsync(); try { var file = FileName(kind, id); var temp = file + "." + Guid.NewGuid() + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value)); File.Move(temp, file, true); } finally { gate.Release(); }
    }
    public async Task<byte[]> Export(string name, byte[] body)
    { using var span = Telemetry.Source.StartActivity("local.blob.put", ActivityKind.Client); await File.WriteAllBytesAsync(Path.Combine(path, name), body); return body; }
}
public sealed class AzureStore : IStore
{
    private readonly Container container;
    private readonly BlobContainerClient blobs;
    public AzureStore(Settings settings)
    {
        var credential = new DefaultAzureCredential();
        var client = new CosmosClient(settings.CosmosEndpoint, credential, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, RequestTimeout = TimeSpan.FromSeconds(3), MaxRetryAttemptsOnRateLimitedRequests = 1, MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(1) });
        container = client.GetContainer(settings.CosmosDatabase, "records");
        blobs = new BlobContainerClient(new Uri($"https://{settings.StorageName}.blob.core.windows.net/exports"), credential);
    }
    public async Task<T?> Get<T>(string kind, string id)
    {
        using var span = Telemetry.Source.StartActivity("cosmos.read", ActivityKind.Client); span?.SetTag("db.system.name", "cosmosdb"); span?.SetTag("db.operation.name", "ReadItem");
        try { var item = await container.ReadItemAsync<Document>(id, new PartitionKey(kind)); span?.SetTag("azure.cosmosdb.request_charge", item.RequestCharge); return JsonSerializer.Deserialize<T>(item.Resource.payload); }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return default; }
    }
    public async Task Put<T>(string kind, string id, T value)
    {
        using var span = Telemetry.Source.StartActivity("cosmos.upsert", ActivityKind.Client); span?.SetTag("db.system.name", "cosmosdb"); span?.SetTag("db.operation.name", "UpsertItem");
        var response = await container.UpsertItemAsync(new Document(id, kind, JsonSerializer.Serialize(value)), new PartitionKey(kind)); span?.SetTag("azure.cosmosdb.request_charge", response.RequestCharge);
    }
    public async Task<byte[]> Export(string name, byte[] body)
    {
        using var span = Telemetry.Source.StartActivity("blob.upload", ActivityKind.Client); span?.SetTag("cloud.provider", "azure");
        using var data = new MemoryStream(body); await blobs.GetBlobClient(name).UploadAsync(data, overwrite: true); return body;
    }
}
public sealed record Workout(string Id, string Name, int Minutes, string Day, bool Seeded);
public sealed record Job(string Id, string WorkoutId, string Status, int Attempts, string? Error, string TraceId, DateTimeOffset UpdatedAt);
public sealed record Control(string Phase, DateTimeOffset ExpiresAt, string Release);
public sealed record EffectiveScenario(string Phase, DateTimeOffset? ExpiresAt, string Release, bool Scheduled);
public sealed class Scenario(IStore store, Settings settings, Telemetry telemetry)
{
    public async Task<EffectiveScenario> Effective(DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var control = await store.Get<Control>("control", "scenario");
        if (control != null && control.ExpiresAt > time) return await Record(new(control.Phase == "regression" ? "regression" : "healthy", control.ExpiresAt, control.Release, false));
        var start = new DateTimeOffset(time.UtcDateTime.Date + TimeOnly.ParseExact(settings.DailyStart, "HH:mm").ToTimeSpan(), TimeSpan.Zero);
        if (time < start) start = start.AddDays(-1);
        var end = start.AddSeconds(settings.WindowSeconds);
        if (time >= start && time < end) return await Record(new("regression", end, $"serializer-v2-{start:yyyyMMdd}", true));
        return await Record(new("healthy", null, $"serializer-v1-{time:yyyyMMdd}", true));
    }
    private async Task<EffectiveScenario> Record(EffectiveScenario state)
    {
        if (await store.Get<string>("control", "last-release") != state.Release)
        {
            await store.Put("control", "last-release", state.Release);
            telemetry.Event("deployment", state.Phase, state.Release, "Effective simulated serializer revision changed (expiry checked on every request/message)");
        }
        return state;
    }
    public async Task<Control> Set(string phase, int seconds)
    {
        if (phase is not ("healthy" or "regression" or "recover") || seconds is < 1 or > 600) throw new ArgumentException("phase healthy|regression|recover and duration_seconds 1..600 required");
        var value = new Control(phase, DateTimeOffset.UtcNow.AddSeconds(seconds), $"serializer-{phase}-{Guid.NewGuid():N}");
        await store.Put("control", "scenario", value);
        telemetry.Event("deployment", phase, value.Release, $"Mocked serializer rollout {phase}; expires {value.ExpiresAt:O}");
        return value;
    }
}
