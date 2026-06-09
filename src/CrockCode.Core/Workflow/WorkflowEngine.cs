using System;
using System.Collections.Immutable;
using System.Diagnostics;
using CrockCode.Core.Domain;

namespace CrockCode.Core.Workflow;

/// <summary>
/// Pure functional workflow engine. All methods are static and side-effect free.
/// <para><c>Decide</c> validates a transition and returns the next state + commands.</para>
/// <para><c>Apply</c> is the pure event-sourcing fold/projection.</para>
/// </summary>
public static class WorkflowEngine
{
    private static readonly ImmutableArray<Command> NoCommands = ImmutableArray<Command>.Empty;

    /// <summary>
    /// Validate an event against the current state and produce the next state + commands.
    /// Returns Err if the transition is invalid.
    /// </summary>
    public static Result<TransitionResult> Decide(WorkflowState state, WorkflowEvent evt, EngineContext ctx)
    {
        // Terminal states reject all events except CancelRequested (which is a no-op if already terminal)
        if (state.IsTerminal)
        {
            if (evt is WorkflowEvent.CancelRequested)
            {
                return Result.Ok(new TransitionResult(state, NoCommands));
            }
            return Result.Err<TransitionResult>(
                new Error.Permanent("ALREADY_TERMINAL",
                    $"Task {state.TaskId} is in terminal state {state.GetType().Name}"));
        }

        return (state, evt) switch
        {
            // ── Queued transitions ──
            (WorkflowState.Queued, WorkflowEvent.Enqueued) =>
                Result.Err<TransitionResult>(
                    new Error.Permanent("ALREADY_QUEUED",
                        $"Task {state.TaskId} is already queued")),

            (WorkflowState.Queued q, WorkflowEvent.WorkerSubmitted ws) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Dispatched(
                        q.TaskId, q.WorkingDir, q.Prompt, q.Priority,
                        q.Attempt, q.MaxAttempts, ws.WorkerId, ws.Timestamp,
                        q.AllowedTools, q.DisallowedTools),
                    NoCommands)),

            (WorkflowState.Queued q, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(q.TaskId, q.WorkingDir, cr.Timestamp),
                    ImmutableArray.Create<Command>(new Command.ReleaseLease(q.WorkingDir, q.TaskId)))),

            // ── Dispatched transitions ──
            (WorkflowState.Dispatched d, WorkflowEvent.TaskClaimed tc) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Running(
                        d.TaskId, d.WorkingDir, d.Prompt, d.Priority,
                        d.Attempt, d.MaxAttempts, tc.WorkerId, tc.LeaseRef, tc.Timestamp,
                        d.AllowedTools, d.DisallowedTools),
                    NoCommands)),

            (WorkflowState.Dispatched d, WorkflowEvent.TransientFailed tf) =>
                HandleTransientFailure(d, tf.Error, tf.Timestamp, ctx),

            (WorkflowState.Dispatched d, WorkflowEvent.WorkerExpired we) =>
                HandleTransientFailure(d, new Error.Transient("WORKER_EXPIRED", $"Worker {we.WorkerId} expired"), we.Timestamp, ctx),

            (WorkflowState.Dispatched d, WorkflowEvent.PermanentFailed pf) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Failed(d.TaskId, d.WorkingDir, pf.Error),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(d.WorkingDir, d.TaskId),
                        new Command.CancelWorker(d.WorkerId)
                    ))),

            (WorkflowState.Dispatched d, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(d.TaskId, d.WorkingDir, cr.Timestamp),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(d.WorkingDir, d.TaskId),
                        new Command.CancelWorker(d.WorkerId)
                    ))),

            // ── Running transitions ──
            (WorkflowState.Running r, WorkflowEvent.CompletionRequested cr) =>
                cr.Status switch
                {
                    CompletionStatus.Success => Result.Ok(new TransitionResult(
                        new WorkflowState.AwaitingSettlement(
                            r.TaskId, r.WorkingDir, r.WorkerId,
                            cr.ResultSummary, new DiffStat(0, 0, 0)),
                        NoCommands)),

                    CompletionStatus.Failure => Result.Ok(new TransitionResult(
                        new WorkflowState.Failed(
                            r.TaskId, r.WorkingDir,
                            new Error.Permanent("WORKER_FAILURE", cr.ResultSummary.Summary)),
                        ImmutableArray.Create<Command>(
                            new Command.ReleaseLease(r.WorkingDir, r.TaskId),
                            new Command.CancelWorker(r.WorkerId)
                        ))),
                    _ => throw new UnreachableException()
                },

            (WorkflowState.Running r, WorkflowEvent.TransientFailed tf) =>
                HandleTransientFailure(r, tf.Error, tf.Timestamp, ctx),

            (WorkflowState.Running r, WorkflowEvent.WorkerExpired we) =>
                HandleTransientFailure(r, new Error.Transient("WORKER_EXPIRED", $"Worker {we.WorkerId} expired"), we.Timestamp, ctx),

            (WorkflowState.Running r, WorkflowEvent.PermanentFailed pf) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Failed(r.TaskId, r.WorkingDir, pf.Error),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(r.WorkingDir, r.TaskId),
                        new Command.CancelWorker(r.WorkerId)
                    ))),

            (WorkflowState.Running r, WorkflowEvent.WorkerSettled) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Failed(r.TaskId, r.WorkingDir,
                        new Error.Permanent("WORKER_SETTLED_WITHOUT_COMPLETION",
                            "Worker batch ended but task was never completed via complete_task")),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(r.WorkingDir, r.TaskId),
                        new Command.CancelWorker(r.WorkerId)
                    ))),

            (WorkflowState.Running r, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(r.TaskId, r.WorkingDir, cr.Timestamp),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(r.WorkingDir, r.TaskId),
                        new Command.CancelWorker(r.WorkerId)
                    ))),

            (WorkflowState.Running r, WorkflowEvent.LeaseExpired le) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Failed(r.TaskId, r.WorkingDir, new Error.Permanent("LEASE_EXPIRED", "Directory lease expired")),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(r.WorkingDir, r.TaskId),
                        new Command.CancelWorker(r.WorkerId)
                    ))),

            (WorkflowState.Running r, WorkflowEvent.AwaitRequested ar) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Suspended(
                        r.TaskId, r.WorkingDir, r.Prompt, r.Priority, r.Attempt, r.MaxAttempts,
                        r.WorkerId, r.LeaseRef, r.StartedAt, ar.AwaitSpec, ar.Checkpoint,
                        r.AllowedTools, r.DisallowedTools),
                    NoCommands)),

            (WorkflowState.Running r, WorkflowEvent.SubtaskEnqueued) =>
                Result.Ok(new TransitionResult(r, NoCommands)),

            // ── Suspended transitions ──
            (WorkflowState.Suspended s, WorkflowEvent.ChildSettled) =>
                Result.Ok(new TransitionResult(s, NoCommands)),

            (WorkflowState.Suspended s, WorkflowEvent.AwaitResolved ar) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Queued(s.TaskId, s.WorkingDir, s.Prompt, s.Priority, s.Attempt, s.MaxAttempts, s.AllowedTools, s.DisallowedTools),
                    NoCommands)),

            (WorkflowState.Suspended s, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(s.TaskId, s.WorkingDir, cr.Timestamp),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(s.WorkingDir, s.TaskId),
                        new Command.CancelWorker(s.WorkerId)
                    ))),

            (WorkflowState.Suspended s, WorkflowEvent.LeaseExpired le) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Failed(s.TaskId, s.WorkingDir, new Error.Permanent("LEASE_EXPIRED", "Directory lease expired")),
                    ImmutableArray.Create<Command>(
                        new Command.ReleaseLease(s.WorkingDir, s.TaskId),
                        new Command.CancelWorker(s.WorkerId)
                    ))),

            // ── AwaitingSettlement transitions ──
            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.WorkerSettled ws) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Completed(
                        aws.TaskId, aws.WorkingDir, aws.ResultSummary, aws.DiffStat, ws.Usage),
                    ImmutableArray.Create<Command>(new Command.ReleaseLease(aws.WorkingDir, aws.TaskId)))),

            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.WorkerExpired) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Completed(
                        aws.TaskId, aws.WorkingDir, aws.ResultSummary, aws.DiffStat, Usage.Zero),
                    ImmutableArray.Create<Command>(new Command.ReleaseLease(aws.WorkingDir, aws.TaskId)))),

            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(aws.TaskId, aws.WorkingDir, cr.Timestamp),
                    ImmutableArray.Create<Command>(new Command.ReleaseLease(aws.WorkingDir, aws.TaskId)))),

            // ── Retrying transitions ──
            (WorkflowState.Retrying r, WorkflowEvent.Tick t) =>
                t.Now >= r.NextAttemptAt
                    ? Result.Ok(new TransitionResult(
                        new WorkflowState.Queued(r.TaskId, r.WorkingDir, r.Prompt, r.Priority, r.Attempt, r.MaxAttempts, r.AllowedTools, r.DisallowedTools),
                        ImmutableArray.Create<Command>(new Command.Requeue(r.TaskId))))
                    : Result.Ok(new TransitionResult(state, NoCommands)),

            (WorkflowState.Retrying r, WorkflowEvent.CancelRequested cr) =>
                Result.Ok(new TransitionResult(
                    new WorkflowState.Cancelled(r.TaskId, r.WorkingDir, cr.Timestamp),
                    NoCommands)),

            // ── General Tick (non-retrying no-op) ──
            (_, WorkflowEvent.Tick) =>
                Result.Ok(new TransitionResult(state, NoCommands)),

            // ── General ProgressReported (no-op transition) ──
            (_, WorkflowEvent.ProgressReported) =>
                Result.Ok(new TransitionResult(state, NoCommands)),

            // ── Invalid transition ──────────────────────────────────────
            _ => Result.Err<TransitionResult>(
                new Error.Permanent("INVALID_TRANSITION",
                    $"No transition defined for {state.GetType().Name} + {evt.GetType().Name}"))
        };
    }

    private static Result<TransitionResult> HandleTransientFailure(WorkflowState state, Error error, Instant now, EngineContext ctx)
    {
        var attempt = state switch
        {
            WorkflowState.Queued q => q.Attempt,
            WorkflowState.Dispatched d => d.Attempt,
            WorkflowState.Running r => r.Attempt,
            WorkflowState.Suspended s => s.Attempt,
            WorkflowState.Retrying rt => rt.Attempt,
            _ => 1
        };

        var maxAttempts = state switch
        {
            WorkflowState.Queued q => q.MaxAttempts,
            WorkflowState.Dispatched d => d.MaxAttempts,
            WorkflowState.Running r => r.MaxAttempts,
            WorkflowState.Suspended s => s.MaxAttempts,
            WorkflowState.Retrying rt => rt.MaxAttempts,
            _ => 3
        };

        var prompt = state switch
        {
            WorkflowState.Queued q => q.Prompt,
            WorkflowState.Dispatched d => d.Prompt,
            WorkflowState.Running r => r.Prompt,
            WorkflowState.Suspended s => s.Prompt,
            WorkflowState.Retrying rt => rt.Prompt,
            _ => ""
        };

        var priority = state switch
        {
            WorkflowState.Queued q => q.Priority,
            WorkflowState.Dispatched d => d.Priority,
            WorkflowState.Running r => r.Priority,
            WorkflowState.Suspended s => s.Priority,
            WorkflowState.Retrying rt => rt.Priority,
            _ => new Priority(0)
        };

        var workerId = state switch
        {
            WorkflowState.Dispatched d => d.WorkerId,
            WorkflowState.Running r => r.WorkerId,
            WorkflowState.Suspended s => s.WorkerId,
            _ => new WorkerId("")
        };

        if (attempt < maxAttempts)
        {
            // Backoff: 15s * 2^(attempt - 1) + jitter
            var backoffSecs = (int)(15 * Math.Pow(2, attempt - 1));
            var jitter = ctx.Random.Next(0, 10);
            var nextAttemptAt = now + TimeSpan.FromSeconds(backoffSecs + jitter);

            var commands = ImmutableArray.CreateBuilder<Command>();
            commands.Add(new Command.ScheduleRetry(state.TaskId, nextAttemptAt, attempt + 1));
            commands.Add(new Command.ReleaseLease(state.WorkingDir, state.TaskId));
            if (!string.IsNullOrEmpty(workerId.Value))
            {
                commands.Add(new Command.CancelWorker(workerId));
            }

            return Result.Ok(new TransitionResult(
                new WorkflowState.Retrying(
                    state.TaskId, state.WorkingDir, prompt, priority,
                    Attempt: attempt + 1, maxAttempts, nextAttemptAt, error,
                    state.AllowedTools, state.DisallowedTools),
                commands.ToImmutable()));
        }
        else
        {
            var commands = ImmutableArray.CreateBuilder<Command>();
            commands.Add(new Command.ReleaseLease(state.WorkingDir, state.TaskId));
            if (!string.IsNullOrEmpty(workerId.Value))
            {
                commands.Add(new Command.CancelWorker(workerId));
            }

            return Result.Ok(new TransitionResult(
                new WorkflowState.Failed(state.TaskId, state.WorkingDir, error),
                commands.ToImmutable()));
        }
    }

    /// <summary>
    /// Pure event-sourcing projection fold. Applies an event to a state to produce the next state.
    /// Used by the Reconciler to rehydrate state from the event log.
    /// Terminal states are absorbing — once terminal, state never changes.
    /// </summary>
    public static WorkflowState Apply(WorkflowState state, WorkflowEvent evt)
    {
        if (state.IsTerminal) return state;

        return (state, evt) switch
        {
            (WorkflowState.Queued q, WorkflowEvent.WorkerSubmitted ws) =>
                new WorkflowState.Dispatched(
                    q.TaskId, q.WorkingDir, q.Prompt, q.Priority,
                    q.Attempt, q.MaxAttempts, ws.WorkerId, ws.Timestamp,
                    q.AllowedTools, q.DisallowedTools),

            (WorkflowState.Queued q, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(q.TaskId, q.WorkingDir, cr.Timestamp),

            (WorkflowState.Dispatched d, WorkflowEvent.TaskClaimed tc) =>
                new WorkflowState.Running(
                    d.TaskId, d.WorkingDir, d.Prompt, d.Priority,
                    d.Attempt, d.MaxAttempts, tc.WorkerId, tc.LeaseRef, tc.Timestamp,
                    d.AllowedTools, d.DisallowedTools),

            (WorkflowState.Dispatched d, WorkflowEvent.TransientFailed tf) =>
                ApplyTransientFailure(d, tf.Error, tf.Timestamp),

            (WorkflowState.Dispatched d, WorkflowEvent.WorkerExpired we) =>
                ApplyTransientFailure(d, new Error.Transient("WORKER_EXPIRED", "Worker expired"), we.Timestamp),

            (WorkflowState.Dispatched d, WorkflowEvent.PermanentFailed pf) =>
                new WorkflowState.Failed(d.TaskId, d.WorkingDir, pf.Error),

            (WorkflowState.Dispatched d, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(d.TaskId, d.WorkingDir, cr.Timestamp),

            (WorkflowState.Running r, WorkflowEvent.CompletionRequested cr) =>
                cr.Status switch
                {
                    CompletionStatus.Success =>
                        new WorkflowState.AwaitingSettlement(
                            r.TaskId, r.WorkingDir, r.WorkerId,
                            cr.ResultSummary, new DiffStat(0, 0, 0)),
                    _ =>
                        new WorkflowState.Failed(
                            r.TaskId, r.WorkingDir,
                            new Error.Permanent("WORKER_FAILURE", cr.ResultSummary.Summary))
                },

            (WorkflowState.Running r, WorkflowEvent.TransientFailed tf) =>
                ApplyTransientFailure(r, tf.Error, tf.Timestamp),

            (WorkflowState.Running r, WorkflowEvent.WorkerExpired we) =>
                ApplyTransientFailure(r, new Error.Transient("WORKER_EXPIRED", "Worker expired"), we.Timestamp),

            (WorkflowState.Running r, WorkflowEvent.PermanentFailed pf) =>
                new WorkflowState.Failed(r.TaskId, r.WorkingDir, pf.Error),

            (WorkflowState.Running r, WorkflowEvent.WorkerSettled) =>
                new WorkflowState.Failed(r.TaskId, r.WorkingDir,
                    new Error.Permanent("WORKER_SETTLED_WITHOUT_COMPLETION",
                        "Worker ended without completing task")),

            (WorkflowState.Running r, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(r.TaskId, r.WorkingDir, cr.Timestamp),

            (WorkflowState.Running r, WorkflowEvent.LeaseExpired) =>
                new WorkflowState.Failed(r.TaskId, r.WorkingDir, new Error.Permanent("LEASE_EXPIRED", "Directory lease expired")),

            (WorkflowState.Running r, WorkflowEvent.AwaitRequested ar) =>
                new WorkflowState.Suspended(
                    r.TaskId, r.WorkingDir, r.Prompt, r.Priority, r.Attempt, r.MaxAttempts,
                    r.WorkerId, r.LeaseRef, r.StartedAt, ar.AwaitSpec, ar.Checkpoint,
                    r.AllowedTools, r.DisallowedTools),

            (WorkflowState.Suspended s, WorkflowEvent.AwaitResolved) =>
                new WorkflowState.Queued(s.TaskId, s.WorkingDir, s.Prompt, s.Priority, s.Attempt, s.MaxAttempts, s.AllowedTools, s.DisallowedTools),

            (WorkflowState.Suspended s, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(s.TaskId, s.WorkingDir, cr.Timestamp),

            (WorkflowState.Suspended s, WorkflowEvent.LeaseExpired) =>
                new WorkflowState.Failed(s.TaskId, s.WorkingDir, new Error.Permanent("LEASE_EXPIRED", "Directory lease expired")),

            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.WorkerSettled ws) =>
                new WorkflowState.Completed(
                    aws.TaskId, aws.WorkingDir, aws.ResultSummary, aws.DiffStat, ws.Usage),

            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.WorkerExpired) =>
                new WorkflowState.Completed(
                    aws.TaskId, aws.WorkingDir, aws.ResultSummary, aws.DiffStat, Usage.Zero),

            (WorkflowState.AwaitingSettlement aws, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(aws.TaskId, aws.WorkingDir, cr.Timestamp),

            (WorkflowState.Retrying r, WorkflowEvent.Tick t) when t.Now >= r.NextAttemptAt =>
                new WorkflowState.Queued(r.TaskId, r.WorkingDir, r.Prompt, r.Priority, r.Attempt, r.MaxAttempts, r.AllowedTools, r.DisallowedTools),

            (WorkflowState.Retrying r, WorkflowEvent.CancelRequested cr) =>
                new WorkflowState.Cancelled(r.TaskId, r.WorkingDir, cr.Timestamp),

            _ => state
        };
    }

    private static WorkflowState ApplyTransientFailure(WorkflowState state, Error error, Instant now)
    {
        var attempt = state switch
        {
            WorkflowState.Queued q => q.Attempt,
            WorkflowState.Dispatched d => d.Attempt,
            WorkflowState.Running r => r.Attempt,
            WorkflowState.Suspended s => s.Attempt,
            WorkflowState.Retrying rt => rt.Attempt,
            _ => 1
        };

        var maxAttempts = state switch
        {
            WorkflowState.Queued q => q.MaxAttempts,
            WorkflowState.Dispatched d => d.MaxAttempts,
            WorkflowState.Running r => r.MaxAttempts,
            WorkflowState.Suspended s => s.MaxAttempts,
            WorkflowState.Retrying rt => rt.MaxAttempts,
            _ => 3
        };

        var prompt = state switch
        {
            WorkflowState.Queued q => q.Prompt,
            WorkflowState.Dispatched d => d.Prompt,
            WorkflowState.Running r => r.Prompt,
            WorkflowState.Suspended s => s.Prompt,
            WorkflowState.Retrying rt => rt.Prompt,
            _ => ""
        };

        var priority = state switch
        {
            WorkflowState.Queued q => q.Priority,
            WorkflowState.Dispatched d => d.Priority,
            WorkflowState.Running r => r.Priority,
            WorkflowState.Suspended s => s.Priority,
            WorkflowState.Retrying rt => rt.Priority,
            _ => new Priority(0)
        };

        if (attempt < maxAttempts)
        {
            // Deterministic backoff computation on Apply (no jitter to keep fold pure without Random)
            var backoffSecs = (int)(15 * Math.Pow(2, attempt - 1));
            var nextAttemptAt = now + TimeSpan.FromSeconds(backoffSecs);

            return new WorkflowState.Retrying(
                state.TaskId, state.WorkingDir, prompt, priority,
                Attempt: attempt + 1, maxAttempts, nextAttemptAt, error,
                state.AllowedTools, state.DisallowedTools);
        }
        else
        {
            return new WorkflowState.Failed(state.TaskId, state.WorkingDir, error);
        }
    }

    /// <summary>
    /// Bootstrap a workflow from an Enqueued event. Creates the initial Queued state.
    /// </summary>
    public static Result<WorkflowState> Initialize(WorkflowEvent.Enqueued evt)
    {
        return Result.Ok<WorkflowState>(new WorkflowState.Queued(
            evt.TaskId, evt.WorkingDir, evt.Prompt, evt.Priority,
            Attempt: 1, evt.MaxAttempts, evt.AllowedTools, evt.DisallowedTools));
    }

    /// <summary>
    /// Replay a sequence of events from an initial state to produce the current state.
    /// </summary>
    public static WorkflowState Replay(WorkflowState initial, IEnumerable<WorkflowEvent> events)
    {
        var state = initial;
        foreach (var evt in events)
        {
            state = Apply(state, evt);
        }
        return state;
    }
}
