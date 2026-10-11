using System.Reflection;

namespace Fitness.Core;
public sealed record Settings
{
    public string Mode { get; init; } = Get("SAMPLE_MODE", "local");
    public string Namespace { get; init; } = Get("OTEL_SERVICE_NAMESPACE", "corridora-samples.fitness");
    public string Environment { get; init; } = Get("DEPLOYMENT_ENVIRONMENT", "development");
    public string Version { get; init; } = typeof(Settings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
    public string Endpoint { get; init; } = Get("CORRIDORA_OTLP_ENDPOINT", "https://ingest.corridora.com").TrimEnd('/');
    public string IngestKey { get; init; } = Get("CORRIDORA_INGEST_KEY");
    public string RumKey { get; init; } = Get("CORRIDORA_RUM_KEY");
    public string RumScript { get; init; } = Get("CORRIDORA_RUM_SCRIPT_URL", "https://rum.corridora.com/corridora-rum.js");
    public string ControlToken { get; init; } = Get("DEMO_CONTROL_TOKEN");
    public string Recommender { get; init; } = Get("RECOMMENDER_URL");
    public string DownstreamToken { get; init; } = Get("DOWNSTREAM_AUTH_TOKEN") is "disabled" ? "" : Get("DOWNSTREAM_AUTH_TOKEN");
    public string DailyStart { get; init; } = Get("DEMO_DAILY_START_UTC", "14:00");
    public int WindowSeconds { get; init; } = int.Parse(Get("DEMO_WINDOW_SECONDS", "600"));
    public string LocalPath { get; init; } = Get("SAMPLE_DATA_DIR", ".local/data");
    public string StorageName { get; init; } = Get("AZURE_STORAGE_ACCOUNT");
    public string CosmosEndpoint { get; init; } = Get("AZURE_COSMOS_ENDPOINT");
    public string CosmosDatabase { get; init; } = Get("AZURE_COSMOS_DATABASE", "fitness");
    public string PublicUrl { get; init; } = Get("PUBLIC_URL", "http://localhost:4104");
    public bool Forwarding { get; init; } = Get("ENABLE_PROVIDER_FORWARDING", "false") == "true";
    public string DiagnosticStorage { get; init; } = Get("AZURE_DIAGNOSTIC_STORAGE_ACCOUNT");
    public string Subscription { get; init; } = Get("AZURE_SUBSCRIPTION_ID");
    public string ResourceGroup { get; init; } = Get("AZURE_RESOURCE_GROUP");
    public string ApiResourceId { get; init; } = Get("AZURE_API_RESOURCE_ID");
    public static string Get(string key, string fallback = "") => System.Environment.GetEnvironmentVariable(key) ?? fallback;
    public void Validate()
    {
        if (WindowSeconds is < 1 or > 600 || !TimeOnly.TryParseExact(DailyStart, "HH:mm", out _)) throw new InvalidOperationException("Daily window must be HH:mm UTC and 1..600 seconds.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http"))) throw new InvalidOperationException("OTLP requires HTTPS (HTTP localhost allowed).");
        if (Mode == "cloud" && (string.IsNullOrWhiteSpace(ControlToken) || IngestKey == "" || RumKey == "" || StorageName == "" || CosmosEndpoint == "")) throw new InvalidOperationException("Cloud requires scoped backend/RUM/control keys, storage and Cosmos configuration.");
        if (Recommender != "" && (!Uri.TryCreate(Recommender, UriKind.Absolute, out var dep) || (dep.Scheme != "https" && !(Mode == "local" && dep.IsLoopback && dep.Scheme == "http")))) throw new InvalidOperationException("Controlled recommender must be HTTPS (HTTP localhost allowed locally).");
    }
}
