using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Demo;
using Yojana.Platform;
using Yojana.Serialisation;
using Yojana.Integration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Contracts;

var cataloguePath = Path.GetFullPath("src/Web/Yojana.Web/DevData/reference-labels.json");
if (!File.Exists(cataloguePath))
{
    Console.Error.WriteLine($"Demo data not loaded: development catalogue not found at '{cataloguePath}'. Run this tool from the repository root.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration["Platform:ConnectionString"] = builder.Configuration["Platform:ConnectionString"]
    ?? "Server=localhost,1433;Database=yojana;User Id=sa;Encrypt=True;TrustServerCertificate=True";
var operationalConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(builder.Configuration["Platform:ConnectionString"]);
operationalConnection.InitialCatalog += "_files";
builder.Configuration["Serialisation:ImportFiles:DatabaseConnectionString"] ??= operationalConnection.ConnectionString;
builder.Configuration["Serialisation:ImportFiles:FileRoot"] ??= Path.Combine(Path.GetTempPath(), "yojana-import-files-demo");
operationalConnection.InitialCatalog = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(builder.Configuration["Platform:ConnectionString"]).InitialCatalog + "_files_archive";
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Name"] ??= "long-term";
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:Kind"] ??= "database";
builder.Configuration["Serialisation:ImportFiles:ArchiveStores:0:ConnectionString"] ??= operationalConnection.ConnectionString;
builder.Configuration["Platform:MigrateOnStartup"] = "false";
builder.Configuration["Platform:AutoBuildMessageStorage"] =
    builder.Configuration["Platform:AutoBuildMessageStorage"] ?? "false";
builder.Configuration["Platform:ServiceName"] = "yojana-demo";
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
PlatformSetup.IncludeHandlers(typeof(IntegrationModule).Assembly);
builder.AddYojanaPlatform();
builder.AddIntegrationModule();
builder.AddSerialisationModule();
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
