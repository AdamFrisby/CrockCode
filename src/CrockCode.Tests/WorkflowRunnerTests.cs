using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using Xunit;

namespace CrockCode.Tests;

public class WorkflowRunnerTests
{
    private readonly FakeEventStore _eventStore = new();
    private readonly FakeProjectionStore _projectionStore = new();
    private readonly FakeClock _clock = new();
    private readonly FakeRandom _random = new();
    private readonly WorkflowRunner _runner;

    public WorkflowRunnerTests()
    {
        _runner = new WorkflowRunner(_eventStore, _projectionStore, _clock, _random);
    }

    private class FakeClock : IClock
    {
        public Instant Now { get; set; } = new(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
    }

    private class FakeRandom : IRandom
    {
        public int Next(int minInclusive, int maxExclusive) => 0;
        public double NextDouble() => 0.0;
    }

    private class FakeEventStore : IEventStore
    {
        public Dictionary<TaskId, List<WorkflowEvent>> Stream { get; } = new();
        public List<TransitionBatch> Appended { get; } = new();

        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct)
        {
            if (Stream.TryGetValue(taskId, out var list))
            {
                return Task.FromResult(Result.Ok((long)list.Count));
            }
            return Task.FromResult(Result.Ok(0L));
        }

        public Task<Result<ImmutableArray<WorkflowEvent>>> LoadEventsAsync(TaskId taskId, long sinceSeq, CancellationToken ct)
        {
            if (Stream.TryGetValue(taskId, out var list))
            {
                return Task.FromResult(Result.Ok(list.ToImmutableArray()));
            }
            return Task.FromResult(Result.Ok(ImmutableArray<WorkflowEvent>.Empty));
        }

        public Task<Result<long>> AppendAsync(TransitionBatch batch, CancellationToken ct)
        {
            Appended.Add(batch);
            if (!Stream.TryGetValue(batch.TaskId, out var list))
            {
                list = new List<WorkflowEvent>();
                Stream[batch.TaskId] = list;
            }
            list.AddRange(batch.Events);
            return Task.FromResult(Result.Ok((long)list.Count));
        }

        public Task<Result<Unit>> AppendAndApplyAsync(TransitionBatch batch, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadEventsAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadSinceAsync(long seq, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class FakeProjectionStore : IProjectionStore
    {
        public Dictionary<TaskId, WorkflowState> States { get; } = new();
        public Dictionary<TaskId, TaskId> Parents { get; } = new();

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default)
        {
            States[taskId] = state;
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default)
        {
            return Task.FromResult(Result.Ok(0L));
        }

        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct)
        {
            Parents.TryGetValue(taskId, out var pId);
            return Task.FromResult(Result.Ok((TaskId?)pId));
        }

        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct)
        {
            States.TryGetValue(taskId, out var state);
            return Task.FromResult(Result.Ok((WorkflowState?)state));
        }

        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default)
        {
            var active = new List<TaskId>();
            foreach (var kv in States)
            {
                if (!kv.Value.IsTerminal)
                {
                    active.Add(kv.Key);
                }
            }
            return Task.FromResult(Result.Ok(active.ToImmutableArray()));
        }
        public Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> UpsertWorkerAsync(WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage, CancellationToken ct = default, string? model = null) => throw new NotImplementedException();
        public Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task ProcessEventAsync_TaskNotFound_OnFirstNonEnqueueEvent()
    {
        var taskId = new TaskId("tsk_missing");
        var evt = new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("wkr_1"), _clock.Now);

        var result = await _runner.ProcessEventAsync(taskId, evt);

        Assert.True(result.IsErr);
        Assert.Equal("TASK_NOT_FOUND", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ProcessEventAsync_Enqueue_BootstrapsTask_Successfully()
    {
        var taskId = new TaskId("tsk_new");
        var evt = new WorkflowEvent.Enqueued(taskId, new WorkingDir("/tmp"), "Do work", new Priority(0), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var result = await _runner.ProcessEventAsync(taskId, evt);

        Assert.True(result.IsOk);
        Assert.Single(_eventStore.Appended);
        Assert.IsType<WorkflowState.Queued>(_eventStore.Appended[0].NextState);
    }

    [Fact]
    public async Task ProcessEventAsync_TerminalState_NotifiesParentTask()
    {
        var parentId = new TaskId("tsk_parent");
        var childId = new TaskId("tsk_child");

        // 1. Enqueue parent
        var pEnq = new WorkflowEvent.Enqueued(parentId, new WorkingDir("/tmp"), "Parent", new Priority(0), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        await _runner.ProcessEventAsync(parentId, pEnq);

        // 2. Transition parent to Running, then Suspended
        var pSubmit = new WorkflowEvent.WorkerSubmitted(parentId, new WorkerId("wkr_p"), _clock.Now);
        await _runner.ProcessEventAsync(parentId, pSubmit);
        var pClaim = new WorkflowEvent.TaskClaimed(parentId, new WorkerId("wkr_p"), new LeaseRef("l_p"), _clock.Now);
        await _runner.ProcessEventAsync(parentId, pClaim);
        
        var spec = new AwaitSpec.AwaitTasksSpec(ImmutableArray.Create(childId));
        var pAwait = new WorkflowEvent.AwaitRequested(parentId, spec, new Checkpoint("blob", 0), _clock.Now);
        await _runner.ProcessEventAsync(parentId, pAwait);

        // Update projections
        var parentState = _eventStore.Appended[^1].NextState;
        _projectionStore.States[parentId] = parentState;

        // Set parent relation
        _projectionStore.Parents[childId] = parentId;

        // 3. Enqueue and Complete child
        var cEnq = new WorkflowEvent.Enqueued(childId, new WorkingDir("/tmp"), "Child", new Priority(0), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        await _runner.ProcessEventAsync(childId, cEnq);
        var cSubmit = new WorkflowEvent.WorkerSubmitted(childId, new WorkerId("wkr_c"), _clock.Now);
        await _runner.ProcessEventAsync(childId, cSubmit);
        var cClaim = new WorkflowEvent.TaskClaimed(childId, new WorkerId("wkr_c"), new LeaseRef("l_c"), _clock.Now);
        await _runner.ProcessEventAsync(childId, cClaim);

        var cComplete = new WorkflowEvent.CompletionRequested(childId, new ResultSummary("Child complete"), CompletionStatus.Success, _clock.Now);
        await _runner.ProcessEventAsync(childId, cComplete);

        // Update child task projection to AwaitingSettlement
        var childAwaitingState = _eventStore.Appended[^1].NextState;
        _projectionStore.States[childId] = childAwaitingState;

        // Transition child task to Completed (terminal state)
        var cWorkerSettled = new WorkflowEvent.WorkerSettled(childId, new WorkerId("wkr_c"), Usage.Zero, _clock.Now);
        _projectionStore.States[childId] = new WorkflowState.Completed(childId, new WorkingDir("/tmp"), new ResultSummary("Child complete"), new DiffStat(0,0,0), Usage.Zero);
        
        var result = await _runner.ProcessEventAsync(childId, cWorkerSettled);

        Assert.True(result.IsOk);
        
        // Let's verify parent was notified and transitioned back to Queued (via AwaitResolved)
        var parentHistory = _eventStore.Stream[parentId];
        Assert.Contains(parentHistory, e => e is WorkflowEvent.ChildSettled);
        Assert.Contains(parentHistory, e => e is WorkflowEvent.AwaitResolved);
    }
}
