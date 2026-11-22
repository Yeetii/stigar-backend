using System.Threading;
using Hangfire.Common;
using Hangfire.Server;

namespace Stigar.Core.Jobs;

/// <summary>
/// Ambient per-job cancellation token for Hangfire targets that do not declare a
/// CancellationToken parameter. Hangfire substitutes its real token only into
/// declared parameters, so adding one here would change the persisted job
/// signature and break invocations already queued in Redis. The server filter
/// opens this scope instead. Outside a job the token is CancellationToken.None,
/// so direct calls (tests, hosted services) keep their current behavior.
/// </summary>
public static class JobCancellation
{
    private static readonly AsyncLocal<CancellationToken> Current = new();

    public static CancellationToken Token => Current.Value;

    public static IDisposable Begin(CancellationToken token)
    {
        var previous = Current.Value;
        Current.Value = token;
        return new Scope(previous);
    }

    private sealed class Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Opens the ambient cancellation scope for the duration of a job. Registered as
/// a global server filter by <see cref="HangfireInfrastructure.AddHangfireStorage"/>,
/// so every job target gets it without any stored payload change.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class JobCancellationServerFilterAttribute : JobFilterAttribute, IServerFilter
{
    private const string ScopeKey = "Stigar.Core.Jobs.JobCancellation.Scope";

    public void OnPerforming(PerformingContext filterContext)
    {
        // A job can carry this filter both globally and as an attribute on older
        // queued invocations. Only the outermost scope may set the ambient token,
        // otherwise the inner scope is lost and the token leaks past the job.
        if (filterContext.Items.ContainsKey(ScopeKey))
            return;

        var token = filterContext.CancellationToken?.ShutdownToken ?? CancellationToken.None;
        filterContext.Items[ScopeKey] = JobCancellation.Begin(token);
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        if (filterContext.Items.TryGetValue(ScopeKey, out var scope) && scope is IDisposable disposable)
        {
            disposable.Dispose();
            filterContext.Items.Remove(ScopeKey);
        }
    }
}
