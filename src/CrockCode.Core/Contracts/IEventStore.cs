using System.Collections.Immutable;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>Batch of state transition + events + commands to persist atomically.</summary>
public sealed record TransitionBatch(
    TaskId TaskId,
    long ExpectedVersion,
    WorkflowState NextState,
    ImmutableArray<WorkflowEvent> Events,
    ImmutableArray<Command> Commands);

/// <summary>
/// Append-only event store for workflow events with optimistic concurrency.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Atomically append a transition batch. Returns Err if the expected version
    /// does not match (optimistic concurrency conflict).
    /// </summary>
    Task<Result<long>> AppendAsync(TransitionBatch batch, CancellationToken ct = default);

    /// <summary>Load all events for a task, optionally from a given version.</summary>
    Task<Result<ImmutableArray<WorkflowEvent>>> LoadEventsAsync(
        TaskId taskId, long fromVersion = 0, CancellationToken ct = default);

    /// <summary>Load the current version number for a task's event stream.</summary>
    Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default);
}
