using Stigar.Core.Jobs;

namespace Stigar.Workers.Tests;

internal sealed class ProbeRecurringJob : IRecurringJob
{
    public string JobId => "probe";
    public string Cron => "0 0 * * *";
    public int Executions { get; private set; }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Executions++;
        return Task.CompletedTask;
    }
}

internal sealed class SecondProbeRecurringJob : IRecurringJob
{
    public string JobId => "probe-2";
    public string Cron => "0 1 * * *";
    public int Executions { get; private set; }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Executions++;
        return Task.CompletedTask;
    }
}
