using Microsoft.Extensions.Hosting;

namespace Yojana.Demo;

public static class DemoProcess
{
    public static async Task<int> RunAsync(
        IHost host,
        Func<CancellationToken, Task> load,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var started = false;
        Exception? failure = null;
        try
        {
            await host.StartAsync(cancellationToken);
            started = true;
            await load(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (started)
            {
                try
                {
                    // Shutdown must not inherit a cancelled load token: hosted schedulers still need a chance to stop.
                    await host.StopAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }

        if (failure is null) return 0;
        await error.WriteLineAsync($"Demo data not loaded: {failure.Message}");
        return 2;
    }
}
