using Fitness.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var settings = new Settings(); settings.Validate();
var host = new HostBuilder().ConfigureFunctionsWorkerDefaults().ConfigureServices(services => {
    services.AddSingleton(settings); services.AddSingleton(new Telemetry(settings, "sample-fitness-worker"));
    services.AddSingleton<IStore, AzureStore>(); services.AddSingleton<Scenario>(); services.AddSingleton<SummaryProcessor>();
    services.AddSingleton<AzureMonitorBridge>();
}).Build();
await host.RunAsync();
