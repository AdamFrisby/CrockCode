using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Workflow;

/// <summary>
/// Closed union representing the states of a task workflow.
/// Uses sealed abstract record hierarchy for exhaustive matching.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Queued), "queued")]
[JsonDerivedType(typeof(Dispatched), "dispatched")]
[JsonDerivedType(typeof(Running), "running")]
[JsonDerivedType(typeof(AwaitingSettlement), "awaiting_settlement")]
[JsonDerivedType(typeof(Completed), "completed")]
[JsonDerivedType(typeof(Failed), "failed")]
[JsonDerivedType(typeof(Suspended), "suspended")]
[JsonDerivedType(typeof(Retrying), "retrying")]
[JsonDerivedType(typeof(Cancelled), "cancelled")]
public abstract record WorkflowState
{
    private WorkflowState() { }

    /// <summary>The task identifier for this state.</summary>
    public abstract TaskId TaskId { get; init; }

    /// <summary>The working directory for this state.</summary>
    public abstract WorkingDir WorkingDir { get; init; }

    /// <summary>Whether this state is terminal.</summary>
    public bool IsTerminal => this is Completed or Failed or Cancelled;

    /// <summary>Optional list of allowed tools. Empty means all allowed.</summary>
    public virtual ImmutableArray<string> AllowedTools { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Optional list of disallowed tools.</summary>
    public virtual ImmutableArray<string> DisallowedTools { get; init; } = ImmutableArray<string>.Empty;

    public abstract T Match<T>(
        Func<Queued, T> queued,
        Func<Dispatched, T> dispatched,
        Func<Running, T> running,
        Func<AwaitingSettlement, T> awaitingSettlement,
        Func<Completed, T> completed,
        Func<Failed, T> failed,
        Func<Suspended, T> suspended,
        Func<Retrying, T> retrying,
        Func<Cancelled, T> cancelled);

    /// <summary>Task has been queued and is waiting for a worker.</summary>
    public sealed record Queued(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int Attempt,
        int MaxAttempts,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools) : WorkflowState
    {
        public override ImmutableArray<string> AllowedTools { get; init; } = AllowedTools;
        public override ImmutableArray<string> DisallowedTools { get; init; } = DisallowedTools;

        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => queued(this);
    }

    /// <summary>A worker has been submitted but has not yet claimed the task.</summary>
    public sealed record Dispatched(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int Attempt,
        int MaxAttempts,
        WorkerId WorkerId,
        Instant DispatchedAt,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools) : WorkflowState
    {
        public override ImmutableArray<string> AllowedTools { get; init; } = AllowedTools;
        public override ImmutableArray<string> DisallowedTools { get; init; } = DisallowedTools;

        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => dispatched(this);
    }

    /// <summary>Worker has claimed the task and is actively working.</summary>
    public sealed record Running(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int Attempt,
        int MaxAttempts,
        WorkerId WorkerId,
        LeaseRef LeaseRef,
        Instant StartedAt,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools) : WorkflowState
    {
        public override ImmutableArray<string> AllowedTools { get; init; } = AllowedTools;
        public override ImmutableArray<string> DisallowedTools { get; init; } = DisallowedTools;

        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => running(this);
    }

    /// <summary>Task completed via complete_task; awaiting worker cost settlement.</summary>
    public sealed record AwaitingSettlement(
        TaskId TaskId,
        WorkingDir WorkingDir,
        WorkerId WorkerId,
        ResultSummary ResultSummary,
        DiffStat DiffStat) : WorkflowState
    {
        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => awaitingSettlement(this);
    }

    /// <summary>Task completed successfully with usage data.</summary>
    public sealed record Completed(
        TaskId TaskId,
        WorkingDir WorkingDir,
        ResultSummary ResultSummary,
        DiffStat DiffStat,
        Usage Usage) : WorkflowState
    {
        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => completed(this);
    }

    /// <summary>Task failed permanently.</summary>
    public sealed record Failed(
        TaskId TaskId,
        WorkingDir WorkingDir,
        Error Reason) : WorkflowState
    {
        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => failed(this);
    }

    /// <summary>Task is suspended awaiting child tasks/subagents.</summary>
    public sealed record Suspended(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int Attempt,
        int MaxAttempts,
        WorkerId WorkerId,
        LeaseRef LeaseRef,
        Instant StartedAt,
        AwaitSpec AwaitSpec,
        Checkpoint Checkpoint,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools) : WorkflowState
    {
        public override ImmutableArray<string> AllowedTools { get; init; } = AllowedTools;
        public override ImmutableArray<string> DisallowedTools { get; init; } = DisallowedTools;

        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => suspended(this);
    }

    /// <summary>Task is waiting for a retry attempt after a transient error.</summary>
    public sealed record Retrying(
        TaskId TaskId,
        WorkingDir WorkingDir,
        string Prompt,
        Priority Priority,
        int Attempt,
        int MaxAttempts,
        Instant NextAttemptAt,
        Error LastError,
        ImmutableArray<string> AllowedTools,
        ImmutableArray<string> DisallowedTools) : WorkflowState
    {
        public override ImmutableArray<string> AllowedTools { get; init; } = AllowedTools;
        public override ImmutableArray<string> DisallowedTools { get; init; } = DisallowedTools;

        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => retrying(this);
    }

    /// <summary>Task was cancelled by the user.</summary>
    public sealed record Cancelled(
        TaskId TaskId,
        WorkingDir WorkingDir,
        Instant CancelledAt) : WorkflowState
    {
        public override T Match<T>(Func<Queued, T> queued, Func<Dispatched, T> dispatched,
            Func<Running, T> running, Func<AwaitingSettlement, T> awaitingSettlement,
            Func<Completed, T> completed, Func<Failed, T> failed,
            Func<Suspended, T> suspended, Func<Retrying, T> retrying, Func<Cancelled, T> cancelled)
            => cancelled(this);
    }
}
