using System;
using System.Collections.Immutable;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using Xunit;

namespace CrockCode.Tests;

public class WorkflowEngineTests
{
    private static readonly TaskId TaskId = new("tsk_123");
    private static readonly WorkingDir WorkingDir = new("/tmp/work");
    private static readonly WorkerId WorkerId = new("wkr_456");
    private static readonly LeaseRef Lease = new("lease_789");
    private static readonly Instant Time0 = new(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
    private static readonly ImmutableArray<string> EmptyTools = ImmutableArray<string>.Empty;

    private class FakeRandom : IRandom
    {
        public int NextValue { get; set; } = 5;
        public int Next(int minInclusive, int maxExclusive) => NextValue;
        public double NextDouble() => 0.5;
    }

    private EngineContext CreateContext(FakeRandom? rand = null)
    {
        return new EngineContext(Time0, rand ?? new FakeRandom());
    }

    // ── DECIDE TESTS (GOOD PATHS) ───────────────────────────────────────────

    [Fact]
    public void Decide_Queued_WorkerSubmitted_TransitionsTo_Dispatched()
    {
        var state = new WorkflowState.Queued(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.WorkerSubmitted(TaskId, WorkerId, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Dispatched>(next);
        var disp = (WorkflowState.Dispatched)next;
        Assert.Equal(WorkerId, disp.WorkerId);
        Assert.Equal(Time0, disp.DispatchedAt);
        Assert.Empty(result.Unwrap().Commands);
    }

    [Fact]
    public void Decide_Dispatched_TaskClaimed_TransitionsTo_Running()
    {
        var state = new WorkflowState.Dispatched(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Time0, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.TaskClaimed(TaskId, WorkerId, Lease, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Running>(next);
        var run = (WorkflowState.Running)next;
        Assert.Equal(Lease, run.LeaseRef);
        Assert.Equal(Time0, run.StartedAt);
        Assert.Empty(result.Unwrap().Commands);
    }

    [Fact]
    public void Decide_Running_CompletionRequested_Success_TransitionsTo_AwaitingSettlement()
    {
        var state = new WorkflowState.Running(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Lease, Time0, EmptyTools, EmptyTools);
        var summary = new ResultSummary("Done successfully");
        var evt = new WorkflowEvent.CompletionRequested(TaskId, summary, CompletionStatus.Success, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.AwaitingSettlement>(next);
        var settled = (WorkflowState.AwaitingSettlement)next;
        Assert.Equal(summary, settled.ResultSummary);
        Assert.Empty(result.Unwrap().Commands);
    }

    [Fact]
    public void Decide_Running_CompletionRequested_Failure_TransitionsTo_Failed_AndReleases_AndCancels()
    {
        var state = new WorkflowState.Running(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Lease, Time0, EmptyTools, EmptyTools);
        var summary = new ResultSummary("Crash");
        var evt = new WorkflowEvent.CompletionRequested(TaskId, summary, CompletionStatus.Failure, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Failed>(next);
        
        var commands = result.Unwrap().Commands;
        Assert.Equal(2, commands.Length);
        Assert.Contains(commands, c => c is Command.ReleaseLease);
        Assert.Contains(commands, c => c is Command.CancelWorker);
    }

    [Fact]
    public void Decide_Running_AwaitRequested_TransitionsTo_Suspended()
    {
        var state = new WorkflowState.Running(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Lease, Time0, EmptyTools, EmptyTools);
        var spec = new AwaitSpec.AwaitTasksSpec(ImmutableArray.Create(new TaskId("subtask")));
        var checkpoint = new Checkpoint("messages", 100);
        var evt = new WorkflowEvent.AwaitRequested(TaskId, spec, checkpoint, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Suspended>(next);
        var susp = (WorkflowState.Suspended)next;
        Assert.Equal(spec, susp.AwaitSpec);
        Assert.Equal(checkpoint, susp.Checkpoint);
    }

    [Fact]
    public void Decide_Suspended_AwaitResolved_TransitionsTo_Queued()
    {
        var spec = new AwaitSpec.AwaitTasksSpec(ImmutableArray.Create(new TaskId("subtask")));
        var checkpoint = new Checkpoint("messages", 100);
        var state = new WorkflowState.Suspended(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Lease, Time0, spec, checkpoint, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.AwaitResolved(TaskId, ImmutableArray<ChildResult>.Empty, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Queued>(next);
    }

    [Fact]
    public void Decide_AwaitingSettlement_WorkerSettled_TransitionsTo_Completed_AndReleasesLease()
    {
        var state = new WorkflowState.AwaitingSettlement(TaskId, WorkingDir, WorkerId, new ResultSummary("done"), new DiffStat(1, 1, 0));
        var usage = new Usage(100, 200, 0.01m);
        var evt = new WorkflowEvent.WorkerSettled(TaskId, WorkerId, usage, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Completed>(next);
        var completed = (WorkflowState.Completed)next;
        Assert.Equal(usage, completed.Usage);

        var commands = result.Unwrap().Commands;
        Assert.Single(commands);
        Assert.IsType<Command.ReleaseLease>(commands[0]);
    }

    [Fact]
    public void Decide_Retrying_Tick_Due_TransitionsTo_Queued_AndRequeues()
    {
        var dueTime = new Instant(Time0.Value - TimeSpan.FromSeconds(5));
        var state = new WorkflowState.Retrying(TaskId, WorkingDir, "Do work", new Priority(10), 2, 3, dueTime, new Error.Transient("err", "t"), EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.Tick(Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        Assert.IsType<WorkflowState.Queued>(result.Unwrap().Next);
        
        var commands = result.Unwrap().Commands;
        Assert.Single(commands);
        Assert.IsType<Command.Requeue>(commands[0]);
    }

    [Fact]
    public void Decide_Retrying_Tick_NotDue_Remains_Retrying_NoCommands()
    {
        var dueTime = new Instant(Time0.Value + TimeSpan.FromSeconds(5));
        var state = new WorkflowState.Retrying(TaskId, WorkingDir, "Do work", new Priority(10), 2, 3, dueTime, new Error.Transient("err", "t"), EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.Tick(Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        Assert.Same(state, result.Unwrap().Next);
        Assert.Empty(result.Unwrap().Commands);
    }

    // ── RESILIENCE & TRANSIENT RETRY DECISIONS ──────────────────────────────

    [Fact]
    public void Decide_Running_TransientFailed_UnderMaxAttempts_TransitionsTo_Retrying_WithJitteredBackoff()
    {
        var state = new WorkflowState.Running(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, WorkerId, Lease, Time0, EmptyTools, EmptyTools);
        var err = new Error.Transient("NET_FLAP", "network flapped");
        var evt = new WorkflowEvent.TransientFailed(TaskId, err, Time0);
        
        var rand = new FakeRandom { NextValue = 7 }; // 7 seconds jitter
        var result = WorkflowEngine.Decide(state, evt, CreateContext(rand));

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Retrying>(next);
        var retrying = (WorkflowState.Retrying)next;
        Assert.Equal(2, retrying.Attempt); // Bumps attempt counter

        // Backoff formula: 15s * 2^(1-1) = 15s. With 7s jitter, total = 22s.
        var expectedDue = Time0 + TimeSpan.FromSeconds(22);
        Assert.Equal(expectedDue, retrying.NextAttemptAt);

        var commands = result.Unwrap().Commands;
        Assert.Equal(3, commands.Length);
        Assert.Contains(commands, c => c is Command.ScheduleRetry);
        Assert.Contains(commands, c => c is Command.ReleaseLease);
        Assert.Contains(commands, c => c is Command.CancelWorker);
    }

    [Fact]
    public void Decide_Running_TransientFailed_AtMaxAttempts_TransitionsTo_Failed()
    {
        // attempt = 3, maxAttempts = 3
        var state = new WorkflowState.Running(TaskId, WorkingDir, "Do work", new Priority(10), 3, 3, WorkerId, Lease, Time0, EmptyTools, EmptyTools);
        var err = new Error.Transient("NET_FLAP", "network flapped");
        var evt = new WorkflowEvent.TransientFailed(TaskId, err, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        var next = result.Unwrap().Next;
        Assert.IsType<WorkflowState.Failed>(next);
        var failed = (WorkflowState.Failed)next;
        Assert.Equal(err, failed.Reason);

        var commands = result.Unwrap().Commands;
        Assert.Equal(2, commands.Length);
        Assert.Contains(commands, c => c is Command.ReleaseLease);
        Assert.Contains(commands, c => c is Command.CancelWorker);
    }

    // ── DECIDE TESTS (BAD PATHS & INVALID STATE TRANSITIONS) ──────────────────

    [Fact]
    public void Decide_Queued_Enqueued_ReturnsErr_AlreadyQueued()
    {
        var state = new WorkflowState.Queued(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.Enqueued(TaskId, WorkingDir, "Do work", new Priority(10), 3, EmptyTools, EmptyTools);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsErr);
        var error = result.UnwrapErr();
        Assert.Equal("ALREADY_QUEUED", error.Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void Decide_Queued_TaskClaimed_ReturnsErr_InvalidTransition()
    {
        var state = new WorkflowState.Queued(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.TaskClaimed(TaskId, WorkerId, Lease, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsErr);
        var error = result.UnwrapErr();
        Assert.Equal("INVALID_TRANSITION", error.Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void Decide_Completed_AnyEvent_ReturnsErr_AlreadyTerminal()
    {
        var state = new WorkflowState.Completed(TaskId, WorkingDir, new ResultSummary("done"), new DiffStat(0, 0, 0), Usage.Zero);
        var evt = new WorkflowEvent.WorkerSubmitted(TaskId, WorkerId, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsErr);
        var error = result.UnwrapErr();
        Assert.Equal("ALREADY_TERMINAL", error.Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void Decide_Failed_CancelRequested_ReturnsOk_Noop_RemainsSameTerminalState()
    {
        var state = new WorkflowState.Failed(TaskId, WorkingDir, new Error.Permanent("code", "reason"));
        var evt = new WorkflowEvent.CancelRequested(TaskId, Time0);

        var result = WorkflowEngine.Decide(state, evt, CreateContext());

        Assert.True(result.IsOk);
        Assert.Same(state, result.Unwrap().Next);
        Assert.Empty(result.Unwrap().Commands);
    }

    // ── APPLY / FOLD EVENT REPLAY TESTS ─────────────────────────────────────

    [Fact]
    public void Apply_WorkerSubmitted_TransitionsTo_Dispatched()
    {
        var state = new WorkflowState.Queued(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, EmptyTools, EmptyTools);
        var evt = new WorkflowEvent.WorkerSubmitted(TaskId, WorkerId, Time0);

        var next = WorkflowEngine.Apply(state, evt);

        Assert.IsType<WorkflowState.Dispatched>(next);
        Assert.Equal(WorkerId, ((WorkflowState.Dispatched)next).WorkerId);
    }

    [Fact]
    public void Apply_Replay_AggregatesEventsCorrectly()
    {
        var initial = new WorkflowState.Queued(TaskId, WorkingDir, "Do work", new Priority(10), 1, 3, EmptyTools, EmptyTools);
        var events = new WorkflowEvent[]
        {
            new WorkflowEvent.WorkerSubmitted(TaskId, WorkerId, Time0),
            new WorkflowEvent.TaskClaimed(TaskId, WorkerId, Lease, Time0),
            new WorkflowEvent.CompletionRequested(TaskId, new ResultSummary("done"), CompletionStatus.Success, Time0),
            new WorkflowEvent.WorkerSettled(TaskId, WorkerId, new Usage(10, 20, 0.001m), Time0)
        };

        var finalState = WorkflowEngine.Replay(initial, events);

        Assert.IsType<WorkflowState.Completed>(finalState);
        var completed = (WorkflowState.Completed)finalState;
        Assert.Equal(new ResultSummary("done"), completed.ResultSummary);
        Assert.Equal(10, completed.Usage.InputTokens);
    }
}
