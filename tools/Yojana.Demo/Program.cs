using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Yojana.Demo;
using Yojana.Platform;
using Yojana.Serialisation;
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
builder.Configuration["Platform:MigrateOnStartup"] = "false";
builder.Configuration["Platform:AutoBuildMessageStorage"] = "false";
builder.Configuration["Platform:ServiceName"] = "yojana-demo";
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
PlatformSetup.IncludeHandlers(typeof(SerialisationModule).Assembly);
builder.AddYojanaPlatform();
builder.AddSerialisationModule();
builder.Services.ConfigureWolverine(options =>
{
    const string coverageQueue = "demo-entitlement-coverage";
    options.PublishMessage<EntitlementCoverageChanged>().ToLocalQueue(coverageQueue);
    options.LocalQueue(coverageQueue).UseDurableInbox().MaximumParallelMessages(1);
});

using var host = builder.Build();
return await DemoProcess.RunAsync(host, async cancellationToken =>
{
    var catalogue = DemoCatalogue.Load(cataloguePath);
    catalogue.Register(host.Services);
    await new DemoLoader(host.Services, catalogue, DateOnly.FromDateTime(DateTime.UtcNow)).RunAsync(cancellationToken);
}, Console.Error, CancellationToken.None);
