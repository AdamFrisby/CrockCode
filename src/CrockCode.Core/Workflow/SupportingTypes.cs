using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Workflow;

// ── Worker Spec ──────────────────────────────────────────────────────

/// <summary>
/// Specification for creating a worker. Supports fresh and resumed workers.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Fresh), "fresh")]
[JsonDerivedType(typeof(Resume), "resume")]
public abstract record WorkerSpec
{
    private WorkerSpec() { }

    public abstract T Match<T>(Func<Fresh, T> fresh, Func<Resume, T> resume);

    /// <summary>Create a new worker from scratch with full configuration.</summary>
    public sealed record Fresh(
        string McpEndpoint,
        string AuthToken,
        string Model,
        int MaxToolTurns,
        string SystemPrompt) : WorkerSpec
    {
        public override T Match<T>(Func<Fresh, T> f, Func<Resume, T> r) => f(this);
    }

    /// <summary>Resume a worker from a previous checkpoint with child task results.</summary>
    public sealed record Resume(
        string McpEndpoint,
        string AuthToken,
        string Model,
        int MaxToolTurns,
        string SystemPrompt,
        Checkpoint Checkpoint,
        ImmutableArray<ChildResult> InjectedResults) : WorkerSpec
    {
        public override T Match<T>(Func<Fresh, T> f, Func<Resume, T> r) => r(this);
    }
}

public sealed record Checkpoint(string MessagesBlob, int Tokens);

public sealed record ChildResult(TaskId TaskId, ResultSummary ResultSummary);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AwaitTasksSpec), "await_tasks")]
public abstract record AwaitSpec
{
    private AwaitSpec() { }
    public abstract T Match<T>(Func<AwaitTasksSpec, T> awaitTasks);

    public sealed record AwaitTasksSpec(ImmutableArray<TaskId> TaskIds) : AwaitSpec
    {
        public override T Match<T>(Func<AwaitTasksSpec, T> awaitTasks) => awaitTasks(this);
    }
}

// ── Worker Handle ────────────────────────────────────────────────────

/// <summary>Handle to a submitted worker, linking WorkerId to provider batch.</summary>
public sealed record WorkerHandle(WorkerId Id, ProviderBatchRef BatchRef);

// ── Worker Status ────────────────────────────────────────────────────

/// <summary>Closed union for worker lifecycle status.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Submitted), "submitted")]
[JsonDerivedType(typeof(InFlight), "in_flight")]
[JsonDerivedType(typeof(Succeeded), "succeeded")]
[JsonDerivedType(typeof(Errored), "errored")]
[JsonDerivedType(typeof(Expired), "expired")]
public abstract record WorkerStatus
{
    private WorkerStatus() { }

    public abstract T Match<T>(
        Func<Submitted, T> submitted,
        Func<InFlight, T> inFlight,
        Func<Succeeded, T> succeeded,
        Func<Errored, T> errored,
        Func<Expired, T> expired);

    public sealed record Submitted : WorkerStatus
    {
        public override T Match<T>(Func<Submitted, T> submitted, Func<InFlight, T> inFlight,
            Func<Succeeded, T> succeeded, Func<Errored, T> errored, Func<Expired, T> expired)
            => submitted(this);
    }

    public sealed record InFlight : WorkerStatus
    {
        public override T Match<T>(Func<Submitted, T> submitted, Func<InFlight, T> inFlight,
            Func<Succeeded, T> succeeded, Func<Errored, T> errored, Func<Expired, T> expired)
            => inFlight(this);
    }

    public sealed record Succeeded : WorkerStatus
    {
        public override T Match<T>(Func<Submitted, T> submitted, Func<InFlight, T> inFlight,
            Func<Succeeded, T> succeeded, Func<Errored, T> errored, Func<Expired, T> expired)
            => succeeded(this);
    }

    public sealed record Errored(Error Error) : WorkerStatus
    {
        public override T Match<T>(Func<Submitted, T> submitted, Func<InFlight, T> inFlight,
            Func<Succeeded, T> succeeded, Func<Errored, T> errored, Func<Expired, T> expired)
            => errored(this);
    }

    public sealed record Expired : WorkerStatus
    {
        public override T Match<T>(Func<Submitted, T> submitted, Func<InFlight, T> inFlight,
            Func<Succeeded, T> succeeded, Func<Errored, T> errored, Func<Expired, T> expired)
            => expired(this);
    }
}

// ── Worker Outcome ───────────────────────────────────────────────────

/// <summary>Final outcome of a worker execution.</summary>
public sealed record WorkerOutcome(WorkerId WorkerId, Usage Usage, WorkerStatus Status);

// ── Result Summary / DiffStat / Usage ────────────────────────────────

/// <summary>Human-readable summary of a completed task.</summary>
public sealed record ResultSummary(string Summary);

/// <summary>Statistics about file changes.</summary>
public sealed record DiffStat(int FilesChanged, int Insertions, int Deletions);

/// <summary>Token and cost usage for an AI operation.</summary>
public sealed record Usage(int InputTokens, int OutputTokens, decimal CostUsd)
{
    /// <summary>Empty usage for initialisation.</summary>
    public static readonly Usage Zero = new(0, 0, 0m);

    /// <summary>Combine two usage records.</summary>
    public Usage Add(Usage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CostUsd + other.CostUsd);
}

// ── Completion Status ────────────────────────────────────────────────

/// <summary>Whether a task completed successfully or failed.</summary>
public enum CompletionStatus
{
    Success,
    Failure
}

// ── Stream Envelope ──────────────────────────────────────────────────

/// <summary>Envelope for streaming events through the event bus.</summary>
public sealed record StreamEnvelope(string Type, TaskId TaskId, long Seq, JsonElement? Data);

// ── Engine Context ───────────────────────────────────────────────────

/// <summary>
/// Context provided to the workflow engine for pure decision-making.
/// Injects the current time and randomness without ambient state.
/// </summary>
public sealed record EngineContext(Instant Now, Contracts.IRandom Random);

// ── Transition Result ────────────────────────────────────────────────

/// <summary>The result of a workflow transition: a new state and zero or more commands.</summary>
public sealed record TransitionResult(WorkflowState Next, ImmutableArray<Command> Commands);

// ── Lease Disposition ────────────────────────────────────────────────

/// <summary>Closed union for the outcome of a lease acquisition attempt.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Acquired), "acquired")]
[JsonDerivedType(typeof(Joined), "joined")]
[JsonDerivedType(typeof(Blocked), "blocked")]
public abstract record LeaseDisposition
{
    private LeaseDisposition() { }

    public abstract T Match<T>(
        Func<Acquired, T> acquired,
        Func<Joined, T> joined,
        Func<Blocked, T> blocked);

    /// <summary>Lease was freshly acquired.</summary>
    public sealed record Acquired(LeaseRef Lease) : LeaseDisposition
    {
        public override T Match<T>(Func<Acquired, T> acquired, Func<Joined, T> joined, Func<Blocked, T> blocked)
            => acquired(this);
    }

    /// <summary>Joined an existing lease.</summary>
    public sealed record Joined(LeaseRef Lease) : LeaseDisposition
    {
        public override T Match<T>(Func<Acquired, T> acquired, Func<Joined, T> joined, Func<Blocked, T> blocked)
            => joined(this);
    }

    /// <summary>Blocked by another task holding the lease.</summary>
    public sealed record Blocked(TaskId HeldBy) : LeaseDisposition
    {
        public override T Match<T>(Func<Acquired, T> acquired, Func<Joined, T> joined, Func<Blocked, T> blocked)
            => blocked(this);
    }
}

// ── Workspace Context ────────────────────────────────────────────────

/// <summary>Context for a workspace operation binding task, directory, and worker.</summary>
public sealed record WorkspaceContext(TaskId TaskId, WorkingDir WorkingDir, WorkerId WorkerId);
