using System.Collections.Immutable;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Channel for receiving external control signals (CLI commands, API calls)
/// and dispatching workflow events into the engine.
/// </summary>
public interface IControlChannel
{
    /// <summary>Enqueue a new task for processing.</summary>
    Task<Result<TaskId>> EnqueueTaskAsync(
        WorkingDir workingDir, string prompt, Priority priority, int maxAttempts,
        ImmutableArray<string> allowedTools, ImmutableArray<string> disallowedTools,
        TaskId? parentId = null,
        CancellationToken ct = default);

    /// <summary>Cancel a running task.</summary>
    Task<Result<bool>> CancelTaskAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>Query the current state of a task.</summary>
    Task<Result<WorkflowState>> GetTaskStateAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>List all tasks.</summary>
    Task<Result<System.Collections.Immutable.ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default);

    /// <summary>Follow the execution event stream.</summary>
    IAsyncEnumerable<StreamEnvelope> FollowStreamAsync(CancellationToken ct = default);

    /// <summary>Retrieve the current public URL of the tunnel provider.</summary>
    Task<Result<string>> GetTunnelUrlAsync(CancellationToken ct = default);

    /// <summary>Trigger an external reachability probe of the tunnel.</summary>
    Task<Result<bool>> ProbeTunnelAsync(CancellationToken ct = default);
}
