using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Yojana.Integration;
using Yojana.Platform;
using Yojana.Serialisation;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class SerialisationPortStartupTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Production_host_lists_the_pdf_label_printer_as_real()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildProductionHost(logs);

        await host.StartAsync();

        Assert.Contains("Port check: 27 ports. Real 12, Unavailable 15, development-only 0.", logs.Messages);
        using var scope = host.Services.CreateScope();
        var row = Assert.Single(PortInventory.Inspect(scope.ServiceProvider).Rows,
            candidate => candidate.PortType == typeof(ILabelPrinterPort));
        Assert.Equal(typeof(Yojana.Serialisation.Labels.TenantConfiguredLabelPrinterPort), row.ImplementationType);
        Assert.Null(row.UnavailableCode);
        Assert.False(row.DevelopmentOnly);

        await host.StopAsync();
    }

    [DatabaseFact]
    public async Task Production_host_rejects_a_development_only_port()
    {
        using var host = BuildProductionHost(new CapturingLoggerProvider(), sneakDevelopmentPartySource: true);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        var details = AllMessages(failure);
        Assert.Contains(nameof(IPartyNamePort), details);
        Assert.Contains(typeof(InMemoryPartyNamePort).FullName!, details);
        Assert.Contains("Development-only Serialisation ports cannot run in Production", details);
    }

    private IHost BuildProductionHost(CapturingLoggerProvider logs, bool sneakDevelopmentPartySource = false)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            fixture.Services.GetRequiredService<IConfiguration>().AsEnumerable());
        builder.Configuration["Platform:MigrateOnStartup"] = "false";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "false";
        builder.Configuration["Platform:StartScheduler"] = "false";
        builder.Configuration["Platform:ServiceName"] = "serialisation-port-startup-tests";
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.AddYojanaPlatform();
        builder.AddSerialisationModule();
        builder.AddIntegrationModule();
        if (sneakDevelopmentPartySource)
            builder.Services.AddKeyedSingleton<IPartyNamePort, InMemoryPartyNamePort>(
                SerialisationPortKeys.PartyNameSource);
        return builder.Build();
    }

    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(Environment.NewLine, messages);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
