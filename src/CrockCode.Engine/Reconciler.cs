using System.Collections.Immutable;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Engine;

public sealed class Reconciler
{
    private readonly IProjectionStore _projectionStore;
    private readonly WorkflowRunner _runner;
    private readonly IClock _clock;

    public Reconciler(
        IProjectionStore projectionStore,
        WorkflowRunner runner,
        IClock clock)
    {
        _projectionStore = projectionStore;
        _runner = runner;
        _clock = clock;
    }

    /// <summary>
    /// Reconciles active workflows on startup.
    /// Feeds a Tick event into each active task to trigger any pending retries,
    /// timeout evaluations, or lease updates.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var activeIdsResult = await _projectionStore.GetActiveTaskIdsAsync(ct);
        if (activeIdsResult is not Result<ImmutableArray<TaskId>>.Ok ok)
        {
            return;
        }

        foreach (var taskId in ok.Value)
        {
            // Trigger a Tick event to let the engine evaluate the state of each active task
            await _runner.ProcessEventAsync(taskId, new WorkflowEvent.Tick(_clock.Now), ct);
        }
    }
}
