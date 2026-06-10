using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using Xunit;

namespace CrockCode.Tests;

public class OutboxDispatcherTests
{
    private readonly FakeOutbox _outbox = new();
    private readonly FakeBatchProvider _batchProvider = new();
    private readonly FakeStreamPublisher _streamPublisher = new();
    private readonly FakeProjectionStore _projectionStore = new();
    private readonly FakeLeaseManager _leaseManager = new();
    private readonly FakeClock _clock = new();
    private readonly List<WorkflowEvent> _processedEvents = new();
    private readonly OutboxDispatcher _dispatcher;

    public OutboxDispatcherTests()
    {
        // Setup a fake runner factory
        Func<WorkflowRunner> runnerFactory = () =>
        {
            var fakeEventStore = new FakeEventStore();
            var fakeRandom = new FakeRandom();
            return new WorkflowRunner(fakeEventStore, _projectionStore, _clock, fakeRandom);
        };

        _dispatcher = new OutboxDispatcher(
            _outbox,
            _batchProvider,
            _streamPublisher,
            _projectionStore,
            _leaseManager,
            runnerFactory,
            _clock
        );
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
        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct) => Task.FromResult(Result.Ok(0L));
        public Task<Result<ImmutableArray<WorkflowEvent>>> LoadEventsAsync(TaskId taskId, long sinceSeq, CancellationToken ct) => Task.FromResult(Result.Ok(ImmutableArray<WorkflowEvent>.Empty));
        public Task<Result<long>> AppendAsync(TransitionBatch batch, CancellationToken ct) => Task.FromResult(Result.Ok(1L));
        public Task<Result<Unit>> AppendAndApplyAsync(TransitionBatch batch, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadEventsAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadSinceAsync(long seq, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class FakeOutbox : IOutbox
    {
        public List<OutboxEntry> Entries { get; } = new();
        public List<CommandId> Acknowledged { get; } = new();

        public Task<Result<bool>> EnqueueAsync(ImmutableArray<Command> commands, CancellationToken ct = default)
        {
            foreach (var cmd in commands)
            {
                Entries.Add(new OutboxEntry(new CommandId(Guid.NewGuid().ToString()), new TaskId("tsk_1"), cmd));
            }
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<ImmutableArray<OutboxEntry>>> DequeueAsync(int maxBatchSize, CancellationToken ct = default)
        {
            var result = Entries.FindAll(e => !Acknowledged.Contains(e.Id));
            return Task.FromResult(Result.Ok(result.ToImmutableArray()));
        }

        public Task<Result<bool>> AcknowledgeAsync(ImmutableArray<CommandId> commandIds, CancellationToken ct = default)
        {
            Acknowledged.AddRange(commandIds);
            return Task.FromResult(Result.Ok(true));
        }
    }

    private class FakeBatchProvider : IBatchProvider
    {
        public List<WorkerSpec> SubmittedSpecs { get; } = new();
        public List<WorkerHandle> CancelledHandles { get; } = new();

        public Task<Result<WorkerHandle>> SubmitAsync(WorkerId idemKey, WorkerSpec spec, CancellationToken ct = default)
        {
            SubmittedSpecs.Add(spec);
            return Task.FromResult(Result.Ok(new WorkerHandle(idemKey, new ProviderBatchRef("batch_123"))));
        }

        public Task<Result<WorkerOutcome>> PollAsync(WorkerHandle handle, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<Result<bool>> CancelAsync(WorkerHandle handle, CancellationToken ct = default)
        {
            CancelledHandles.Add(handle);
            return Task.FromResult(Result.Ok(true));
        }
    }

    private class FakeStreamPublisher : IStreamEventPublisher
    {
        public List<StreamEnvelope> Published { get; } = new();

        public Task<Result<Unit>> PublishAsync(StreamEnvelope envelope, CancellationToken ct = default)
        {
            Published.Add(envelope);
            return Task.FromResult(Result.Ok(Unit.Value));
        }
    }

    private class FakeProjectionStore : IProjectionStore
    {
        public List<(WorkerId WorkerId, ProviderBatchRef BatchRef, WorkerStatus Status)> SavedWorkers { get; } = new();
        public ProviderBatchRef? MockBatchRef { get; set; }

        public Task<Result<bool>> UpsertWorkerAsync(WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage, CancellationToken ct = default, string? model = null)
        {
            SavedWorkers.Add((workerId, batchRef, status));
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default)
        {
            return Task.FromResult(Result.Ok(MockBatchRef));
        }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok((WorkflowState?)null));
        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(0L));
        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<TaskId>.Empty));
        public Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default) => Task.FromResult(Result.Ok(0));
        public Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<WorkflowState>.Empty));
        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default) => Task.FromResult(Result.Ok((WorkflowState?)null));
        public Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<WorkerHandle>.Empty));
        public Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default) => Task.FromResult(Result.Ok((string?)null));
        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok((TaskId?)null));
        public Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(((Checkpoint, ImmutableArray<ChildResult>)?)null));
        public Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<ToolCallRecord>.Empty));
    }

    private class FakeLeaseManager : ILeaseManager
    {
        public List<LeaseRef> ReleasedLeases { get; } = new();

        public Task<Result<LeaseDisposition>> AcquireAsync(WorkingDir workingDir, TaskId rootTaskId, TimeSpan ttl, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> RenewAsync(LeaseRef leaseRef, TimeSpan ttl, CancellationToken ct = default) => throw new NotImplementedException();
        
        public Task<Result<bool>> ReleaseAsync(LeaseRef leaseRef, CancellationToken ct = default)
        {
            ReleasedLeases.Add(leaseRef);
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> IsHeldAsync(LeaseRef leaseRef, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<int>> ReapExpiredLeasesAsync(Instant now, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task DispatchPendingAsync_NoPendingEntries_DoesNothing()
    {
        await _dispatcher.DispatchPendingAsync();

        Assert.Empty(_outbox.Acknowledged);
    }

    [Fact]
    public async Task DispatchPendingAsync_EmitStreamEvent_PublishesAndAcknowledges()
    {
        var envelope = new StreamEnvelope("TestEvent", new TaskId("tsk_1"), 1L, null);
        var command = new Command.EmitStreamEvent(envelope);
        var entry = new OutboxEntry(new CommandId("cmd_1"), new TaskId("tsk_1"), command);
        _outbox.Entries.Add(entry);

        await _dispatcher.DispatchPendingAsync();

        Assert.Single(_streamPublisher.Published);
        Assert.Equal(envelope, _streamPublisher.Published[0]);
        
        Assert.Single(_outbox.Acknowledged);
        Assert.Equal(entry.Id, _outbox.Acknowledged[0]);
    }

    [Fact]
    public async Task DispatchPendingAsync_SubmitWorker_SubmitsAndAcknowledges()
    {
        var spec = new WorkerSpec.Fresh("http://mcp", "token", "model", 10, "prompt");
        var command = new Command.SubmitWorker(new WorkerId("wkr_1"), spec);
        var entry = new OutboxEntry(new CommandId("cmd_2"), new TaskId("tsk_1"), command);
        _outbox.Entries.Add(entry);

        await _dispatcher.DispatchPendingAsync();

        Assert.Single(_batchProvider.SubmittedSpecs);
        Assert.Equal(spec, _batchProvider.SubmittedSpecs[0]);

        Assert.Single(_projectionStore.SavedWorkers);
        Assert.Equal(new WorkerId("wkr_1"), _projectionStore.SavedWorkers[0].WorkerId);
        Assert.IsType<WorkerStatus.InFlight>(_projectionStore.SavedWorkers[0].Status);

        Assert.Single(_outbox.Acknowledged);
    }

    [Fact]
    public async Task DispatchPendingAsync_ReleaseLease_ReleasesAndAcknowledges()
    {
        var command = new Command.ReleaseLease(new WorkingDir("/tmp"), new TaskId("tsk_1"));
        var entry = new OutboxEntry(new CommandId("cmd_3"), new TaskId("tsk_1"), command);
        _outbox.Entries.Add(entry);

        await _dispatcher.DispatchPendingAsync();

        Assert.Single(_leaseManager.ReleasedLeases);
        Assert.Equal("tsk_1", _leaseManager.ReleasedLeases[0].Value);

        Assert.Single(_outbox.Acknowledged);
    }

    [Fact]
    public async Task DispatchPendingAsync_CancelWorker_CancelsAndAcknowledges()
    {
        var workerId = new WorkerId("wkr_1");
        _projectionStore.MockBatchRef = new ProviderBatchRef("batch_123");

        var command = new Command.CancelWorker(workerId);
        var entry = new OutboxEntry(new CommandId("cmd_4"), new TaskId("tsk_1"), command);
        _outbox.Entries.Add(entry);

        await _dispatcher.DispatchPendingAsync();

        Assert.Single(_batchProvider.CancelledHandles);
        Assert.Equal(workerId, _batchProvider.CancelledHandles[0].Id);
        Assert.Equal("batch_123", _batchProvider.CancelledHandles[0].BatchRef.Value);

        Assert.Single(_outbox.Acknowledged);
    }
}
