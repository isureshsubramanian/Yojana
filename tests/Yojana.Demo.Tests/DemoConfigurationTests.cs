using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yojana.Demo.Tests;

public sealed class DemoConfigurationTests
{
    [Fact]
    public void Demo_writes_to_the_stores_the_development_web_host_reads()
    {
        var root = RepositoryRoot();
        var web = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, DemoConfiguration.WebProjectPath, "appsettings.Development.json"))
            .Build();
        var demo = new ConfigurationManager();

        DemoConfiguration.Apply(demo, root, []);

        foreach (var key in new[]
                 {
                     "Platform:ConnectionString",
                     "Serialisation:ImportFiles:DatabaseConnectionString",
                     "Serialisation:ImportFiles:FileRoot",
                 })
            Assert.Equal(web[key], demo[key]);
        var webStores = web.GetSection("Serialisation:ImportFiles:ArchiveStores").GetChildren()
            .Select(store => (store["Name"], store["Kind"], store["Path"], store["ConnectionString"])).ToList();
        var demoStores = demo.GetSection("Serialisation:ImportFiles:ArchiveStores").GetChildren()
            .Select(store => (store["Name"], store["Kind"], store["Path"], store["ConnectionString"])).ToList();
        Assert.NotEmpty(webStores);
        Assert.Equal(webStores, demoStores);
        Assert.Equal("true", demo["Platform:AutoBuildMessageStorage"], ignoreCase: true);
        Assert.Equal("false", demo["Platform:MigrateOnStartup"]);
    }

    [Fact]
    public void Command_line_overrides_still_beat_the_web_settings()
    {
        var demo = new ConfigurationManager();

        DemoConfiguration.Apply(demo, RepositoryRoot(), ["--Serialisation:ImportFiles:FileRoot=/elsewhere"]);

        Assert.Equal("/elsewhere", demo["Serialisation:ImportFiles:FileRoot"]);
    }

    [Fact]
    public void Missing_web_settings_stop_the_demo_instead_of_guessing_stores()
    {
        var empty = Directory.CreateTempSubdirectory("yojana-demo-config-");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => DemoConfiguration.Apply(new ConfigurationManager(), empty.FullName, []));
            Assert.Contains("appsettings.Development.json", error.Message);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Demo_runs_as_development_unless_told_otherwise()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))) return;

        Assert.Equal(Environments.Development, DemoConfiguration.Settings([]).EnvironmentName);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, DemoConfiguration.WebProjectPath)))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found above the test output folder.");
    }
}
