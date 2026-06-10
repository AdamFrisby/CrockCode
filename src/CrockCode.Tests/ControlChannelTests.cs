using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using CrockCode.Coordinator;
using Xunit;

namespace CrockCode.Tests;

public class ControlChannelTests
{
    private class FakeIdFactory : IIdFactory
    {
        public TaskId NextTaskId { get; set; } = new TaskId("tsk_123");
        public WorkerId NextWorkerId { get; set; } = new WorkerId("wkr_123");
        public CommandId NextCommandId { get; set; } = new CommandId("cmd_123");
        public LeaseRef NextLeaseRef { get; set; } = new LeaseRef("lease_123");

        public TaskId NewTaskId() => NextTaskId;
        public WorkerId NewWorkerId() => NextWorkerId;
        public CommandId NewCommandId() => NextCommandId;
        public LeaseRef NewLeaseRef() => NextLeaseRef;
    }

    private class FakeClock : IClock
    {
        public Instant Now { get; set; } = new Instant(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
    }

    private class FakeTunnelProvider : ITunnelProvider
    {
        public Result<PublicEndpoint> StartResult { get; set; } = Result.Ok(new PublicEndpoint("https://tunnel.example.com"));
        public Result<bool> ProbeResult { get; set; } = Result.Ok(true);
        public bool StopCalled { get; private set; }

        public Task<Result<PublicEndpoint>> StartAsync(int localPort, CancellationToken ct = default)
        {
            return Task.FromResult(StartResult);
        }

        public Task<Result<bool>> ProbeAsync(PublicEndpoint endpoint, CancellationToken ct = default)
        {
            return Task.FromResult(ProbeResult);
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalled = true;
            return Task.CompletedTask;
        }
    }

    private class FakeProjectionStore : IProjectionStore
    {
        public Func<TaskId, CancellationToken, Task<Result<WorkflowState?>>>? LoadFunc { get; set; }
        public Func<CancellationToken, Task<Result<ImmutableArray<WorkflowState>>>>? ListFunc { get; set; }

        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct)
        {
            if (LoadFunc != null) return LoadFunc(taskId, ct);
            return Task.FromResult(Result.Ok<WorkflowState?>(null));
        }

        public Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default)
        {
            if (ListFunc != null) return ListFunc(ct);
            return Task.FromResult(Result.Ok(ImmutableArray<WorkflowState>.Empty));
        }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct) => Task.FromResult(Result.Ok<TaskId?>(null));
        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<bool>> UpsertWorkerAsync(WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage, CancellationToken ct = default, string? model = null) => throw new NotImplementedException();
        public Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class FakeEventStore : IEventStore
    {
        public Func<TaskId, CancellationToken, Task<Result<long>>>? GetVersionFunc { get; set; }
        public Func<TransitionBatch, CancellationToken, Task<Result<long>>>? AppendFunc { get; set; }
        public Func<TaskId, long, CancellationToken, Task<Result<ImmutableArray<WorkflowEvent>>>>? LoadEventsFunc { get; set; }

        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct)
        {
            if (GetVersionFunc != null) return GetVersionFunc(taskId, ct);
            return Task.FromResult(Result.Ok(0L));
        }

        public Task<Result<long>> AppendAsync(TransitionBatch batch, CancellationToken ct)
        {
            if (AppendFunc != null) return AppendFunc(batch, ct);
            return Task.FromResult(Result.Ok(1L));
        }

        public Task<Result<ImmutableArray<WorkflowEvent>>> LoadEventsAsync(TaskId taskId, long sinceSeq, CancellationToken ct)
        {
            if (LoadEventsFunc != null) return LoadEventsFunc(taskId, sinceSeq, ct);
            return Task.FromResult(Result.Ok(ImmutableArray<WorkflowEvent>.Empty));
        }

        public Task<Result<Unit>> AppendAndApplyAsync(TransitionBatch batch, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadEventsAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowEvent>>> ReadSinceAsync(long seq, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class FakeRandom : IRandom
    {
        public int Next(int minInclusive, int maxExclusive) => 0;
        public double NextDouble() => 0.0;
    }

    private (ControlChannel, FakeIdFactory, FakeClock, FakeTunnelProvider, FakeProjectionStore, FakeEventStore) CreateSystem()
    {
        var idFactory = new FakeIdFactory();
        var clock = new FakeClock();
        var tunnelProvider = new FakeTunnelProvider();
        var projectionStore = new FakeProjectionStore();
        var eventStore = new FakeEventStore();
        var runner = new WorkflowRunner(eventStore, projectionStore, clock, new FakeRandom());
        var config = new CrockConfig
        {
            LocalPort = 9090,
            WarmIdleBuffer = 1,
            MaxConcurrency = 2,
            Model = "gpt-4o",
            IdleTimeoutSeconds = 60
        };
        var channel = new ControlChannel(runner, projectionStore, idFactory, clock, tunnelProvider, config);
        return (channel, idFactory, clock, tunnelProvider, projectionStore, eventStore);
    }

    [Fact]
    public async Task EnqueueTaskAsync_Success_ReturnsTaskId()
    {
        var (channel, idFactory, _, _, _, eventStore) = CreateSystem();
        bool appendCalled = false;
        eventStore.AppendFunc = (batch, ct) =>
        {
            appendCalled = true;
            Assert.Equal(idFactory.NextTaskId, batch.TaskId);
            Assert.Single(batch.Events);
            Assert.IsType<WorkflowEvent.Enqueued>(batch.Events[0]);
            return Task.FromResult(Result.Ok(1L));
        };

        var result = await channel.EnqueueTaskAsync(
            new WorkingDir("/home/user"), "Prompt", new Priority(1), 3,
            ImmutableArray.Create("tool1"), ImmutableArray.Create("tool2")
        );

        Assert.True(result.IsOk);
        Assert.Equal(idFactory.NextTaskId, result.Unwrap());
        Assert.True(appendCalled);
    }

    [Fact]
    public async Task CancelTaskAsync_Success_ReturnsTrue()
    {
        var (channel, _, _, _, _, eventStore) = CreateSystem();
        var taskId = new TaskId("tsk_to_cancel");
        bool appendCalled = false;

        // Setup EventStore so that the task exists (since seq > 0) and is in Dispatched state
        eventStore.GetVersionFunc = (id, ct) => Task.FromResult(Result.Ok(2L));
        eventStore.LoadEventsFunc = (id, since, ct) => Task.FromResult(Result.Ok(ImmutableArray.Create<WorkflowEvent>(
            new WorkflowEvent.Enqueued(taskId, new WorkingDir("/home"), "Prompt", new Priority(1), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("wkr_1"), new Instant(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero)))
        )));

        eventStore.AppendFunc = (batch, ct) =>
        {
            appendCalled = true;
            Assert.Equal(taskId, batch.TaskId);
            Assert.Single(batch.Events);
            var evt = batch.Events[0];
            Assert.IsType<WorkflowEvent.PermanentFailed>(evt);
            var failed = (WorkflowEvent.PermanentFailed)evt;
            Assert.Equal("CANCELLED", failed.Error.Match(t => t.Code, p => p.Code));
            return Task.FromResult(Result.Ok(2L));
        };

        var result = await channel.CancelTaskAsync(taskId);

        Assert.True(result.IsOk);
        Assert.True(result.Unwrap());
        Assert.True(appendCalled);
    }

    [Fact]
    public async Task GetTaskStateAsync_StateFound_ReturnsState()
    {
        var (channel, _, _, _, projectionStore, _) = CreateSystem();
        var taskId = new TaskId("tsk_1");
        var expectedState = new WorkflowState.Queued(
            taskId, new WorkingDir("/home"), "Prompt", new Priority(1), 1, 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        projectionStore.LoadFunc = (id, ct) => Task.FromResult(Result.Ok<WorkflowState?>(expectedState));

        var result = await channel.GetTaskStateAsync(taskId);

        Assert.True(result.IsOk);
        Assert.Equal(expectedState, result.Unwrap());
    }

    [Fact]
    public async Task GetTaskStateAsync_StateNotFound_ReturnsError()
    {
        var (channel, _, _, _, projectionStore, _) = CreateSystem();
        var taskId = new TaskId("tsk_missing");

        projectionStore.LoadFunc = (id, ct) => Task.FromResult(Result.Ok<WorkflowState?>(null));

        var result = await channel.GetTaskStateAsync(taskId);

        Assert.True(result.IsErr);
        Assert.Equal("TASK_NOT_FOUND", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task GetTaskStateAsync_LoadError_ReturnsError()
    {
        var (channel, _, _, _, projectionStore, _) = CreateSystem();
        var taskId = new TaskId("tsk_error");

        projectionStore.LoadFunc = (id, ct) => Task.FromResult(Result.Err<WorkflowState?>(new Error.Permanent("DB_ERROR", "Read failure")));

        var result = await channel.GetTaskStateAsync(taskId);

        Assert.True(result.IsErr);
        Assert.Equal("DB_ERROR", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ListTasksAsync_Success_ReturnsList()
    {
        var (channel, _, _, _, projectionStore, _) = CreateSystem();
        var list = ImmutableArray.Create<WorkflowState>(
            new WorkflowState.Queued(new TaskId("tsk_1"), new WorkingDir("/home"), "Prompt", new Priority(1), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)
        );

        projectionStore.ListFunc = ct => Task.FromResult(Result.Ok(list));

        var result = await channel.ListTasksAsync();

        Assert.True(result.IsOk);
        Assert.Single(result.Unwrap());
    }

    [Fact]
    public void FollowStreamAsync_ThrowsNotSupportedException()
    {
        var (channel, _, _, _, _, _) = CreateSystem();

        Assert.Throws<NotSupportedException>(() => channel.FollowStreamAsync());
    }

    [Fact]
    public async Task GetTunnelUrlAsync_Success_ReturnsUrl()
    {
        var (channel, _, _, tunnelProvider, _, _) = CreateSystem();

        var result = await channel.GetTunnelUrlAsync();

        Assert.True(result.IsOk);
        Assert.Equal("https://tunnel.example.com", result.Unwrap());
    }

    [Fact]
    public async Task ProbeTunnelAsync_Success_ReturnsTrue()
    {
        var (channel, _, _, tunnelProvider, _, _) = CreateSystem();
        tunnelProvider.StartResult = Result.Ok(new PublicEndpoint("https://tunnel.example.com"));
        tunnelProvider.ProbeResult = Result.Ok(true);

        var result = await channel.ProbeTunnelAsync();

        Assert.True(result.IsOk);
        Assert.True(result.Unwrap());
    }

    [Fact]
    public async Task ProbeTunnelAsync_StartFailure_ReturnsErr()
    {
        var (channel, _, _, tunnelProvider, _, _) = CreateSystem();
        tunnelProvider.StartResult = new Result<PublicEndpoint>.Err(new Error.Permanent("TUNNEL_FAIL", "Start failed"));

        var result = await channel.ProbeTunnelAsync();

        Assert.True(result.IsErr);
        Assert.Equal("TUNNEL_FAIL", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }
}
