using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Fitness.Core;
public sealed class Telemetry : IDisposable
{
    public static readonly ActivitySource Source = new("sample-fitness");
    public static readonly Meter Meter = new("sample-fitness");
    public static readonly Counter<long> Jobs = Meter.CreateCounter<long>("fitness.summary.attempts");
    public static readonly Counter<long> Workouts = Meter.CreateCounter<long>("fitness.workouts.read");
    public static readonly Histogram<double> ProviderMetric = Meter.CreateHistogram<double>("azure.platform.metric");
    private readonly TracerProvider traces;
    private readonly MeterProvider metrics;
    private readonly ILoggerFactory logs;
    private readonly ServiceProvider logServices;
    private readonly LoggerProvider logProvider;
    public ILogger Log { get; }
    public static int ExportFailures => Volatile.Read(ref exportFailures);
    private static int exportFailures;
    public Telemetry(Settings settings, string service)
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        var resource = ResourceBuilder.CreateDefault().AddService(service, settings.Namespace, settings.Version)
            .AddAttributes(new Dictionary<string, object> { ["deployment.environment.name"] = settings.Environment, ["cloud.provider"] = settings.Mode == "cloud" ? "azure" : "local", ["cloud.platform"] = settings.Mode == "cloud" ? (service.EndsWith("worker") ? "azure_functions" : "azure_container_apps") : "local", ["cloud.region"] = Settings.Get("AZURE_LOCATION", "local"), ["cloud.resource_id"] = service.EndsWith("worker") ? Settings.Get("AZURE_WORKER_RESOURCE_ID", "local") : Settings.Get("AZURE_API_RESOURCE_ID", "local") });
        void Export(OtlpExporterOptions options, string signal)
        {
            options.Endpoint = new Uri($"{settings.Endpoint}/v1/{signal}");
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
            options.Headers = "X-CORRIDORA-KEY=" + Uri.EscapeDataString(settings.IngestKey);
            options.TimeoutMilliseconds = 1500;
            options.HttpClientFactory = () => new HttpClient(new ExportHandler()) { Timeout = TimeSpan.FromMilliseconds(1500) };
        }
        var traceBuilder = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).SetSampler(new ParentBasedSampler(new AlwaysOnSampler())).AddSource("sample-fitness");
        if (settings.IngestKey != "") traceBuilder.AddOtlpExporter(o => Export(o, "traces"));
        traces = traceBuilder.Build();
        var metricBuilder = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter("sample-fitness");
        if (settings.IngestKey != "") metricBuilder.AddOtlpExporter(o => Export(o, "metrics"));
        metrics = metricBuilder.Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddConsole().AddOpenTelemetry(o => { o.SetResourceBuilder(resource); o.IncludeFormattedMessage = true; o.IncludeScopes = true; if (settings.IngestKey != "") o.AddOtlpExporter(e => Export(e, "logs")); }));
        logServices = services.BuildServiceProvider();
        logs = logServices.GetRequiredService<ILoggerFactory>();
        logProvider = logServices.GetRequiredService<LoggerProvider>();
        Log = logs.CreateLogger(service);
    }
    public void Event(string name, string scenario, string release, string message)
    {
        using var scope = Log.BeginScope(new Dictionary<string, object> { ["event.name"] = name, ["demo.simulated"] = true, ["demo.scenario"] = "serializer-poison-summary", ["demo.phase"] = scenario, ["demo.release"] = release });
        Log.LogInformation("{Message}", message);
    }
    public bool Flush()
    {
        return traces.ForceFlush(750) & metrics.ForceFlush(750) & logProvider.ForceFlush(750);
    }
    public void Dispose() { traces.Dispose(); metrics.Dispose(); logServices.Dispose(); }
    private sealed class ExportHandler : HttpClientHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                var response = await base.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) { Interlocked.Increment(ref exportFailures); Console.Error.WriteLine($"OTLP export rejected ({(int)response.StatusCode}); inspect endpoint/key."); }
                return response;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Interlocked.Increment(ref exportFailures); Console.Error.WriteLine("OTLP export unavailable: " + e.GetType().Name); throw; }
        }
    }
}
