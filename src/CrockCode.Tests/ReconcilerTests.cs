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

public class ReconcilerTests
{
    private readonly FakeEventStore _eventStore = new();
    private readonly FakeProjectionStore _projectionStore = new();
    private readonly FakeClock _clock = new();
    private readonly FakeRandom _random = new();
    private readonly WorkflowRunner _runner;
    private readonly Reconciler _reconciler;

    public ReconcilerTests()
    {
        _runner = new WorkflowRunner(_eventStore, _projectionStore, _clock, _random);
        _reconciler = new Reconciler(_projectionStore, _runner, _clock);
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
        public bool ShouldFailActiveTasks { get; set; }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default)
        {
            States[taskId] = state;
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(0L));
        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct) => Task.FromResult(Result.Ok((TaskId?)null));
        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct)
        {
            States.TryGetValue(taskId, out var state);
            return Task.FromResult(Result.Ok((WorkflowState?)state));
        }

        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default)
        {
            if (ShouldFailActiveTasks)
            {
                return Task.FromResult<Result<ImmutableArray<TaskId>>>(new Result<ImmutableArray<TaskId>>.Err(new Error.Permanent("FAIL", "Simulated database failure")));
            }
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

        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
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
    public async Task ReconcileAsync_Empty_DoesNothing()
    {
        await _reconciler.ReconcileAsync();
        Assert.Empty(_eventStore.Appended);
    }

    [Fact]
    public async Task ReconcileAsync_Error_ReturnsEarly()
    {
        _projectionStore.ShouldFailActiveTasks = true;
        await _reconciler.ReconcileAsync();
        Assert.Empty(_eventStore.Appended);
    }

    [Fact]
    public async Task ReconcileAsync_ActiveTasks_ProcessesTickEvents()
    {
        var taskId1 = new TaskId("task_1");
        var taskId2 = new TaskId("task_2");

        var enq1 = new WorkflowEvent.Enqueued(taskId1, new WorkingDir("/tmp"), "Prompt 1", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        var enq2 = new WorkflowEvent.Enqueued(taskId2, new WorkingDir("/tmp"), "Prompt 2", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        await _runner.ProcessEventAsync(taskId1, enq1);
        await _runner.ProcessEventAsync(taskId2, enq2);

        _projectionStore.States[taskId1] = _eventStore.Appended[0].NextState;
        _projectionStore.States[taskId2] = _eventStore.Appended[1].NextState;

        _eventStore.Appended.Clear();

        await _reconciler.ReconcileAsync();

        Assert.Equal(2, _eventStore.Appended.Count);
        Assert.Contains(_eventStore.Appended, batch => batch.TaskId == taskId1 && batch.Events[0] is WorkflowEvent.Tick);
        Assert.Contains(_eventStore.Appended, batch => batch.TaskId == taskId2 && batch.Events[0] is WorkflowEvent.Tick);
    }
}
