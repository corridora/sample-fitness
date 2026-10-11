using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace Fitness.Core;
public sealed record SummaryMessage(string JobId, string WorkoutId, string TraceParent, string TraceState, DateTimeOffset ExpiresAt, string Release);
public interface ISummaryQueue { Task Send(SummaryMessage message); }
public sealed class AzureSummaryQueue(Settings settings) : ISummaryQueue
{
    private readonly QueueClient queue = new(new Uri($"https://{settings.StorageName}.queue.core.windows.net/weekly-summary"), new DefaultAzureCredential(), new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
    public async Task Send(SummaryMessage message) { await queue.SendMessageAsync(JsonSerializer.Serialize(message), timeToLive: TimeSpan.FromMinutes(15)); }
}
public sealed class LocalSummaryQueue : ISummaryQueue
{
    public Channel<SummaryMessage> Messages { get; } = Channel.CreateBounded<SummaryMessage>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.Wait });
    public async Task Send(SummaryMessage message) => await Messages.Writer.WriteAsync(message);
}
public sealed class SummaryService(IStore store, Scenario scenario, ISummaryQueue queue)
{
    public async Task<Job> Enqueue(string workoutId)
    {
        var effective = await scenario.Effective();
        using var producer = Telemetry.Source.StartActivity("weekly-summary enqueue", ActivityKind.Producer);
        producer?.SetTag("messaging.system", "azure_queue_storage"); producer?.SetTag("messaging.destination.name", "weekly-summary"); producer?.SetTag("demo.release", effective.Release);
        var id = Guid.NewGuid().ToString("N");
        var job = new Job(id, workoutId, "queued", 0, null, producer?.TraceId.ToHexString() ?? "", DateTimeOffset.UtcNow);
        await store.Put("job", id, job);
        var context = Activity.Current;
        var message = new SummaryMessage(id, workoutId, context?.Id ?? "", context?.TraceStateString ?? "", effective.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10), effective.Release);
        await queue.Send(message);
        return job;
    }
}
public sealed class SummaryProcessor(IStore store, Scenario scenario, Telemetry telemetry)
{
    public async Task Process(SummaryMessage message, int attempt)
    {
        ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var parent);
        using var consumer = Telemetry.Source.StartActivity("weekly-summary process", ActivityKind.Consumer, parent);
        consumer?.SetTag("messaging.system", "azure_queue_storage"); consumer?.SetTag("messaging.message.id", message.JobId); consumer?.SetTag("messaging.destination.name", "weekly-summary"); consumer?.SetTag("messaging.message.retry_count", attempt - 1);
        var current = await scenario.Effective();
        consumer?.SetTag("demo.scenario", "serializer-poison-summary"); consumer?.SetTag("demo.phase", current.Phase); consumer?.SetTag("demo.release", current.Release);
        Telemetry.Jobs.Add(1, new KeyValuePair<string, object?>("demo.phase", current.Phase));
        try
        {
            // Only the named synthetic fixture is incompatible. Both message and current override expire.
            if (message.WorkoutId == "demo-workout" && current.Phase == "regression" && message.ExpiresAt > DateTimeOffset.UtcNow)
                _ = JsonSerializer.Deserialize<StrictSummary>("{\"Minutes\":\"serializer-v2-bug\"}");
            await store.Put("job", message.JobId, new Job(message.JobId, message.WorkoutId, "complete", attempt, null, consumer?.TraceId.ToHexString() ?? "", DateTimeOffset.UtcNow));
            telemetry.Log.LogInformation("Summary {JobId} completed on attempt {Attempt}", message.JobId, attempt);
        }
        catch (JsonException)
        {
            consumer?.SetStatus(ActivityStatusCode.Error, "seeded serializer regression");
            await store.Put("job", message.JobId, new Job(message.JobId, message.WorkoutId, attempt >= 3 ? "poison" : "retrying", attempt, "Seeded serializer-v2 incompatibility", consumer?.TraceId.ToHexString() ?? "", DateTimeOffset.UtcNow));
            telemetry.Log.LogWarning("Seeded summary {JobId} rejected, attempt {Attempt}; bounded max 3", message.JobId, attempt);
            throw;
        }
        finally { consumer?.Stop(); telemetry.Flush(); }
    }
    private sealed record StrictSummary(int Minutes);
    public async Task Poison(SummaryMessage message)
    {
        ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var parent);
        using var span = Telemetry.Source.StartActivity("weekly-summary poison", ActivityKind.Consumer, parent);
        span?.SetStatus(ActivityStatusCode.Error, "bounded retry exhausted");
        await store.Put("poison", message.JobId, message);
        telemetry.Log.LogWarning("Summary {JobId} moved to weekly-summary-poison; replay creates a fresh message after recovery", message.JobId);
        span?.Stop(); telemetry.Flush();
    }
}
