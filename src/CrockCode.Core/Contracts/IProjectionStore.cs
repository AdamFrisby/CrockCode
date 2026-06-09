using System.Collections.Immutable;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Read-model store for materialised projections of workflow state.
/// Provides fast lookups without replaying events.
/// </summary>
public interface IProjectionStore
{
    /// <summary>Save or update a workflow state projection.</summary>
    Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default);

    /// <summary>Load the current projected state for a task.</summary>
    Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>Load the current version for a task's projection.</summary>
    Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>Gets all active (non-terminal) task IDs.</summary>
    Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default);

    /// <summary>Gets the count of queued tasks that are currently dispatchable (not blocked by directory leases from other task trees).</summary>
    Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default);

    /// <summary>List all tasks in the projection store.</summary>
    Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default);

    /// <summary>Load the task currently assigned to the given worker.</summary>
    Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default);

    /// <summary>Gets all active in-flight worker handles.</summary>
    Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default);

    /// <summary>Upserts a worker record with its status and token usage.</summary>
    Task<Result<bool>> UpsertWorkerAsync(
        WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage,
        CancellationToken ct = default, string? model = null);

    /// <summary>Load the model name assigned to the given worker.</summary>
    Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default);

    /// <summary>Load the batch reference for a given worker.</summary>
    Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default);

    /// <summary>Load the parent task ID of a given task.</summary>
    Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>Load the checkpoint and child results for a task that is resuming.</summary>
    Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default);

    /// <summary>Record a tool call and its result to the task transcript.</summary>
    Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default);

    /// <summary>Get all recorded tool calls for a task.</summary>
    Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default);
}

public sealed record ToolCallRecord(string ToolName, string ArgumentsJson, string ResultJson);
