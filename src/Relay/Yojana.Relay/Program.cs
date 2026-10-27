using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Relay;
using Yojana.Relay.Contracts;

var configPath = RelayCommandLine.ConfigurationPath(args);
IRelaySecretProtection protection = OperatingSystem.IsWindows()
    ? new DpapiRelaySecretProtection() : new FileRelaySecretProtection();
var secretFile = new RelaySecretFile(protection);
var secretPath = configPath + ".secrets";
if (args.Contains("--protect-secrets", StringComparer.Ordinal))
{
    // Provisioning is run by an administrator at a console: report a refusal in one line, never a stack trace.
    try
    {
        var secrets = JsonSerializer.Deserialize<RelaySecrets>(await Console.In.ReadToEndAsync(), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("relay_secrets_invalid");
        secretFile.Write(secretPath, secrets);
    }
    catch (RelaySecretFileExistsException exception)
    {
        await Console.Error.WriteLineAsync(exception.Message);
        Environment.ExitCode = 2;
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException)
    {
        await Console.Error.WriteLineAsync("The secrets on standard input are not one valid JSON object (relaySecret, company, userName, password).");
        Environment.ExitCode = 2;
    }
    return;
}
var configuration = JsonSerializer.Deserialize<RelayAgentConfiguration>(await File.ReadAllTextAsync(configPath), JsonSerializerOptions.Web)
    ?? throw new InvalidDataException("relay_configuration_invalid");
var builder = Host.CreateDefaultBuilder().UseWindowsService(options => options.ServiceName = "YojanaRelay");
builder.ConfigureServices(services =>
{
    services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
    services.AddSingleton(configuration);
    services.AddSingleton(secretFile.Read(secretPath));
    services.AddSingleton(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) });
    services.AddSingleton<RelayCloudClient>();
    services.AddSingleton<RelayLocalClient>();
    services.AddHostedService<RelayAgent>();
    services.AddHostedService<RelayWebhookListener>();
});
await builder.Build().RunAsync();
