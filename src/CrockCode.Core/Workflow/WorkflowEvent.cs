using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Workflow;

/// <summary>
/// Closed union representing events that drive workflow state transitions.
/// Decorated with JsonPolymorphic for event-sourcing persistence with stable discriminators.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Enqueued), "enqueued")]
[JsonDerivedType(typeof(WorkerSubmitted), "worker_submitted")]
[JsonDerivedType(typeof(TaskClaimed), "task_claimed")]
[JsonDerivedType(typeof(CompletionRequested), "completion_requested")]
[JsonDerivedType(typeof(WorkerSettled), "worker_settled")]
[JsonDerivedType(typeof(WorkerExpired), "worker_expired")]
[JsonDerivedType(typeof(TransientFailed), "transient_failed")]
[JsonDerivedType(typeof(PermanentFailed), "permanent_failed")]
[JsonDerivedType(typeof(Tick), "tick")]
[JsonDerivedType(typeof(SubtaskEnqueued), "subtask_enqueued")]
[JsonDerivedType(typeof(AwaitRequested), "await_requested")]
[JsonDerivedType(typeof(ChildSettled), "child_settled")]
[JsonDerivedType(typeof(AwaitResolved), "await_resolved")]
[JsonDerivedType(typeof(CancelRequested), "cancel_requested")]
[JsonDerivedType(typeof(LeaseExpired), "lease_expired")]
[JsonDerivedType(typeof(ProgressReported), "progress_reported")]
public abstract record WorkflowEvent
{
    private WorkflowEvent() { }

    public abstract T Match<T>(
        Func<Enqueued, T> enqueued,
        Func<WorkerSubmitted, T> workerSubmitted,
        Func<TaskClaimed, T> taskClaimed,
        Func<CompletionRequested, T> completionRequested,
        Func<WorkerSettled, T> workerSettled,
        Func<WorkerExpired, T> workerExpired,
        Func<TransientFailed, T> transientFailed,
        Func<PermanentFailed, T> permanentFailed,
        Func<Tick, T> tick,
        Func<SubtaskEnqueued, T> subtaskEnqueued,
        Func<AwaitRequested, T> awaitRequested,
        Func<ChildSettled, T> childSettled,
        Func<AwaitResolved, T> awaitResolved,
        Func<CancelRequested, T> cancelRequested,
        Func<LeaseExpired, T> leaseExpired);

    /// <summary>A new task was enqueued for processing.</summary>
    public sealed record Enqueued(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int MaxAttempts,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools,
        TaskId? ParentId = null) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => enqueued(this);
    }

    /// <summary>A worker was submitted (created) for a task.</summary>
    public sealed record WorkerSubmitted(
        TaskId TaskId,
        WorkerId WorkerId,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => workerSubmitted(this);
    }

    /// <summary>A worker claimed the task and began execution.</summary>
    public sealed record TaskClaimed(
        TaskId TaskId,
        WorkerId WorkerId,
        LeaseRef LeaseRef,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => taskClaimed(this);
    }

    /// <summary>The worker reported completion (success or failure).</summary>
    public sealed record CompletionRequested(
        TaskId TaskId,
        ResultSummary ResultSummary,
        CompletionStatus Status,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => completionRequested(this);
    }

    /// <summary>Worker usage was settled (token/cost accounting). Settles the *worker*, not the task.</summary>
    public sealed record WorkerSettled(
        TaskId TaskId,
        WorkerId WorkerId,
        Usage Usage,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => workerSettled(this);
    }

    /// <summary>Worker expired (provider 24h cap or timeout). After completion this is benign.</summary>
    public sealed record WorkerExpired(
        TaskId TaskId,
        WorkerId WorkerId,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => workerExpired(this);
    }

    /// <summary>A transient (potentially retryable) error occurred.</summary>
    public sealed record TransientFailed(
        TaskId TaskId,
        Error Error,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => transientFailed(this);
    }

    /// <summary>A permanent (non-retryable) error occurred.</summary>
    public sealed record PermanentFailed(
        TaskId TaskId,
        Error Error,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => permanentFailed(this);
    }

    /// <summary>Heartbeat tick for timeout/lease expiry detection and pool decisions.</summary>
    public sealed record Tick(Instant Now) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => tick(this);
    }

    /// <summary>A subtask has been enqueued by a parent.</summary>
    public sealed record SubtaskEnqueued(
        TaskId ParentId,
        TaskId ChildId,
        string Prompt,
        Priority Priority) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => subtaskEnqueued(this);
    }

    /// <summary>A task has requested suspension to await child results.</summary>
    public sealed record AwaitRequested(
        TaskId TaskId,
        AwaitSpec AwaitSpec,
        Checkpoint Checkpoint,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => awaitRequested(this);
    }

    /// <summary>A child task has settled (reached terminal state).</summary>
    public sealed record ChildSettled(
        TaskId ParentId,
        TaskId ChildId,
        ResultSummary ResultSummary,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => childSettled(this);
    }

    /// <summary>An await condition has been resolved.</summary>
    public sealed record AwaitResolved(
        TaskId TaskId,
        ImmutableArray<ChildResult> Results,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => await_resolved(awaitResolved);

        // Map to awaitResolved to avoid mismatch with C# case naming standard
        private T await_resolved<T>(Func<AwaitResolved, T> awaitResolved) => awaitResolved(this);
    }

    /// <summary>Cancellation of a task has been requested.</summary>
    public sealed record CancelRequested(
        TaskId TaskId,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => cancelRequested(this);
    }

    /// <summary>A directory lease has expired.</summary>
    public sealed record LeaseExpired(
        WorkingDir WorkingDir,
        TaskId TaskId,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => leaseExpired(this);
    }

    /// <summary>Progress was reported for a task.</summary>
    public sealed record ProgressReported(
        TaskId TaskId,
        string Message,
        Instant Timestamp) : WorkflowEvent
    {
        public override T Match<T>(Func<Enqueued, T> enqueued, Func<WorkerSubmitted, T> workerSubmitted,
            Func<TaskClaimed, T> taskClaimed, Func<CompletionRequested, T> completionRequested,
            Func<WorkerSettled, T> workerSettled, Func<WorkerExpired, T> workerExpired,
            Func<TransientFailed, T> transientFailed, Func<PermanentFailed, T> permanentFailed,
            Func<Tick, T> tick, Func<SubtaskEnqueued, T> subtaskEnqueued, Func<AwaitRequested, T> awaitRequested,
            Func<ChildSettled, T> childSettled, Func<AwaitResolved, T> awaitResolved,
            Func<CancelRequested, T> cancelRequested, Func<LeaseExpired, T> leaseExpired)
            => default!;
    }
}
