using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Demo;
using Yojana.Platform;
using Yojana.Serialisation;
using Yojana.Integration;
using Yojana.Knowledge;
using Yojana.Knowledge.Questions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Contracts;

var cataloguePath = Path.GetFullPath("src/Web/Yojana.Web/DevData/reference-labels.json");
if (!File.Exists(cataloguePath))
{
    Console.Error.WriteLine($"Demo data not loaded: development catalogue not found at '{cataloguePath}'. Run this tool from the repository root.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(DemoConfiguration.Settings(args));
DemoConfiguration.Apply(builder.Configuration, Directory.GetCurrentDirectory(), args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
PlatformSetup.IncludeHandlers(typeof(IntegrationModule).Assembly);
PlatformSetup.IncludeHandlers(typeof(KnowledgeModule).Assembly);
// The console host needs routing services for the platform authorization policy cache in Development.
builder.Services.AddRouting();
builder.AddYojanaPlatform();
builder.AddIntegrationModule();
builder.AddSerialisationModule();
builder.AddSerialisationDevelopmentPorts();
builder.AddKnowledgeModule();
// The Codex SQL Express image has no full-text feature. The demo adapter keeps the production implementation when
// available and crosses this existing search seam only for the same LIKE-based fallback used by database tests.
builder.Services.Replace(ServiceDescriptor.Scoped<IQuestionTextSearch, DemoQuestionTextSearch>());
// Demo loading deliberately supplies its in-memory source records through the same narrow ports the Integration
// module implements in production. These replacements make that boundary explicit; module registration order must
// not decide which source the loader reads, or receipts and item profiles silently disappear from repeatable demos.
builder.Services.Replace(ServiceDescriptor.Singleton<IPurchasingDocumentQueryPort>(services => services.GetRequiredService<InMemoryDocumentPorts>()));
builder.Services.Replace(ServiceDescriptor.Singleton<IManufacturingDocumentQueryPort>(services => services.GetRequiredService<InMemoryDocumentPorts>()));
builder.Services.Replace(ServiceDescriptor.Singleton<IInventoryDocumentQueryPort>(services => services.GetRequiredService<InMemoryDocumentPorts>()));
builder.Services.Replace(ServiceDescriptor.Singleton<ISalesDocumentQueryPort>(services => services.GetRequiredService<InMemoryDocumentPorts>()));
builder.Services.Replace(ServiceDescriptor.Singleton<IItemQueryPort>(services => services.GetRequiredService<InMemoryItemPorts>()));

using var host = builder.Build();
return await DemoProcess.RunAsync(host, async cancellationToken =>
{
    var catalogue = DemoCatalogue.Load(cataloguePath);
    catalogue.Register(host.Services);
    await new DemoLoader(host.Services, catalogue, DateOnly.FromDateTime(DateTime.UtcNow)).RunAsync(cancellationToken);
}, Console.Error, CancellationToken.None);
