using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using CrockCode.Coordinator;
using Xunit;

namespace CrockCode.Tests;

public class PoolManagerServiceTests
{
    private class FakeHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopCalled { get; private set; }
        public void StopApplication() => StopCalled = true;
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

    private class FakeTokenSigner : ITokenSigner
    {
        public Result<WorkspaceToken> Sign(WorkspaceContext context, TimeSpan ttl)
        {
            return Result.Ok(new WorkspaceToken("dummy_token_" + context.WorkerId.Value));
        }

        public Result<WorkspaceContext> Verify(WorkspaceToken token)
        {
            return Result.Ok(new WorkspaceContext(new TaskId("tsk_1"), new WorkingDir("/tmp"), new WorkerId("wkr_1")));
        }
    }

    private class FakeTunnelProvider : ITunnelProvider
    {
        public Result<PublicEndpoint> StartResult { get; set; } = Result.Ok(new PublicEndpoint("https://tunnel.example.com"));
        public Result<bool> ProbeResult { get; set; } = Result.Ok(true);

        public Task<Result<PublicEndpoint>> StartAsync(int localPort, CancellationToken ct = default) => Task.FromResult(StartResult);
        public Task<Result<bool>> ProbeAsync(PublicEndpoint endpoint, CancellationToken ct = default) => Task.FromResult(ProbeResult);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private class FakeOutbox : IOutbox
    {
        public Task<Result<bool>> EnqueueAsync(ImmutableArray<Command> commands, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<ImmutableArray<OutboxEntry>>> DequeueAsync(int maxBatchSize, CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<OutboxEntry>.Empty));
        public Task<Result<bool>> AcknowledgeAsync(ImmutableArray<CommandId> commandIds, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
    }

    private class FakeLeaseManager : ILeaseManager
    {
        public Task<Result<LeaseDisposition>> AcquireAsync(WorkingDir workingDir, TaskId taskId, TimeSpan ttl, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> RenewAsync(LeaseRef leaseRef, TimeSpan ttl, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> ReleaseAsync(LeaseRef leaseRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> IsHeldAsync(LeaseRef leaseRef, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
    }

    private class FakeStreamPublisher : IStreamEventPublisher
    {
        public Task<Result<Unit>> PublishAsync(StreamEnvelope envelope, CancellationToken ct = default) => Task.FromResult(Result.Ok(Unit.Value));
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

    private class FakeBatchProvider : IBatchProvider
    {
        public Func<WorkerId, WorkerSpec, CancellationToken, Task<Result<WorkerHandle>>>? SubmitFunc { get; set; }
        public Func<WorkerHandle, CancellationToken, Task<Result<WorkerOutcome>>>? PollFunc { get; set; }

        public Task<Result<WorkerHandle>> SubmitAsync(WorkerId idemKey, WorkerSpec spec, CancellationToken ct = default)
        {
            if (SubmitFunc != null) return SubmitFunc(idemKey, spec, ct);
            return Task.FromResult(Result.Ok(new WorkerHandle(idemKey, new ProviderBatchRef("batch_123"))));
        }

        public Task<Result<WorkerOutcome>> PollAsync(WorkerHandle handle, CancellationToken ct = default)
        {
            if (PollFunc != null) return PollFunc(handle, ct);
            return Task.FromResult(Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.InFlight())));
        }

        public Task<Result<bool>> CancelAsync(WorkerHandle handle, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
    }

    private class FakeProjectionStore : IProjectionStore
    {
        public Func<CancellationToken, Task<Result<ImmutableArray<TaskId>>>>? GetActiveTaskIdsFunc { get; set; }
        public Func<CancellationToken, Task<Result<ImmutableArray<WorkerHandle>>>>? GetActiveWorkersFunc { get; set; }
        public Func<WorkerId, CancellationToken, Task<Result<WorkflowState?>>>? LoadByWorkerFunc { get; set; }
        public Func<Instant, CancellationToken, Task<Result<int>>>? GetDispatchableTaskCountFunc { get; set; }
        public Func<CancellationToken, Task<Result<ImmutableArray<WorkflowState>>>>? ListTasksFunc { get; set; }
        public Func<TaskId, CancellationToken, Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>>>? GetResumeDataFunc { get; set; }

        public List<(WorkerId WorkerId, ProviderBatchRef BatchRef, WorkerStatus Status)> SavedWorkers { get; } = new();

        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default)
        {
            if (GetActiveTaskIdsFunc != null) return GetActiveTaskIdsFunc(ct);
            return Task.FromResult(Result.Ok(ImmutableArray<TaskId>.Empty));
        }

        public Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default)
        {
            if (GetActiveWorkersFunc != null) return GetActiveWorkersFunc(ct);
            return Task.FromResult(Result.Ok(ImmutableArray<WorkerHandle>.Empty));
        }

        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default)
        {
            if (LoadByWorkerFunc != null) return LoadByWorkerFunc(workerId, ct);
            return Task.FromResult(Result.Ok((WorkflowState?)null));
        }

        public Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default)
        {
            if (GetDispatchableTaskCountFunc != null) return GetDispatchableTaskCountFunc(now, ct);
            return Task.FromResult(Result.Ok(0));
        }

        public Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default)
        {
            if (ListTasksFunc != null) return ListTasksFunc(ct);
            return Task.FromResult(Result.Ok(ImmutableArray<WorkflowState>.Empty));
        }

        public Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default)
        {
            if (GetResumeDataFunc != null) return GetResumeDataFunc(taskId, ct);
            return Task.FromResult(Result.Ok(((Checkpoint, ImmutableArray<ChildResult>)?)null));
        }

        public Task<Result<bool>> UpsertWorkerAsync(WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage, CancellationToken ct = default, string? model = null)
        {
            SavedWorkers.Add((workerId, batchRef, status));
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok((WorkflowState?)null));
        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(0L));
        public Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default) => Task.FromResult(Result.Ok((string?)null));
        public Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default) => Task.FromResult(Result.Ok((ProviderBatchRef?)null));
        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok((TaskId?)null));
        public Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default) => Task.FromResult(Result.Ok(true));
        public Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(ImmutableArray<ToolCallRecord>.Empty));
    }

    private (PoolManagerService Service, FakeHostLifetime Lifetime, FakeProjectionStore ProjectionStore, FakeBatchProvider BatchProvider, FakeClock Clock) CreateSystem(CrockConfig config)
    {
        var projectionStore = new FakeProjectionStore();
        var eventStore = new FakeEventStore();
        var batchProvider = new FakeBatchProvider();
        var clock = new FakeClock();
        var streamPublisher = new FakeStreamPublisher();
        var leaseManager = new FakeLeaseManager();
        var idFactory = new FakeIdFactory();
        var tokenSigner = new FakeTokenSigner();
        var tunnelProvider = new FakeTunnelProvider();
        var lifetime = new FakeHostLifetime();

        var outboxDispatcher = new OutboxDispatcher(
            new FakeOutbox(),
            batchProvider,
            streamPublisher,
            projectionStore,
            leaseManager,
            () => new WorkflowRunner(eventStore, projectionStore, clock, new FakeRandom()),
            clock
        );

        var runner = new WorkflowRunner(eventStore, projectionStore, clock, new FakeRandom());

        var service = new PoolManagerService(
            projectionStore,
            eventStore,
            batchProvider,
            outboxDispatcher,
            runner,
            clock,
            tokenSigner,
            idFactory,
            tunnelProvider,
            config,
            lifetime,
            NullLogger<PoolManagerService>.Instance
        );

        return (service, lifetime, projectionStore, batchProvider, clock);
    }

    [Fact]
    public async Task PoolManagerService_IdleShutdown_Works()
    {
        var config = new CrockConfig
        {
            IdleTimeoutSeconds = 5
        };
        var (service, lifetime, _, _, _) = CreateSystem(config);

        // Pre-populate _idleSince to simulate that it has been idle for 10 seconds already
        var field = typeof(PoolManagerService).GetField("_idleSince", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(service, DateTime.UtcNow.AddSeconds(-10));

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        await runTask;

        Assert.True(lifetime.StopCalled);
    }

    [Fact]
    public async Task PoolManagerService_PollActiveWorkers_ProcessesTerminalStates()
    {
        var config = new CrockConfig
        {
            IdleTimeoutSeconds = 300,
            MaxConcurrency = 2,
            WarmIdleBuffer = 0
        };
        var (service, lifetime, projectionStore, batchProvider, _) = CreateSystem(config);

        var handle1 = new WorkerHandle(new WorkerId("wkr_succeeded"), new ProviderBatchRef("batch_s"));
        var handle2 = new WorkerHandle(new WorkerId("wkr_errored"), new ProviderBatchRef("batch_err"));
        var handle3 = new WorkerHandle(new WorkerId("wkr_expired"), new ProviderBatchRef("batch_exp"));
        var handle4 = new WorkerHandle(new WorkerId("wkr_inflight"), new ProviderBatchRef("batch_inf"));

        projectionStore.GetActiveWorkersFunc = ct => Task.FromResult(Result.Ok(ImmutableArray.Create(handle1, handle2, handle3, handle4)));

        batchProvider.PollFunc = (handle, ct) =>
        {
            if (handle.Id.Value == "wkr_succeeded")
                return Task.FromResult(Result.Ok(new WorkerOutcome(handle.Id, new Usage(100, 200, 5), new WorkerStatus.Succeeded())));
            if (handle.Id.Value == "wkr_errored")
                return Task.FromResult(Result.Ok(new WorkerOutcome(handle.Id, new Usage(10, 20, 1), new WorkerStatus.Errored(new Error.Permanent("FAIL", "Generic fail")))));
            if (handle.Id.Value == "wkr_expired")
                return Task.FromResult(Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.Expired())));
            return Task.FromResult(Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.InFlight())));
        };

        // Tasks assigned to workers
        projectionStore.LoadByWorkerFunc = (wkrId, ct) =>
        {
            var taskId = new TaskId("tsk_" + wkrId.Value);
            return Task.FromResult(Result.Ok<WorkflowState?>(new WorkflowState.Queued(
                taskId, new WorkingDir("/tmp"), "Prompt", new Priority(1), 1, 3,
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
            )));
        };

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);

        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Verify terminal state worker statuses are updated in the projection store
        Assert.Contains(projectionStore.SavedWorkers, w => w.WorkerId.Value == "wkr_succeeded" && w.Status is WorkerStatus.Succeeded);
        Assert.Contains(projectionStore.SavedWorkers, w => w.WorkerId.Value == "wkr_errored" && w.Status is WorkerStatus.Errored);
        Assert.Contains(projectionStore.SavedWorkers, w => w.WorkerId.Value == "wkr_expired" && w.Status is WorkerStatus.Expired);
        // In-flight worker status is not updated because it's not terminal
        Assert.DoesNotContain(projectionStore.SavedWorkers, w => w.WorkerId.Value == "wkr_inflight");
    }

    [Fact]
    public async Task PoolManagerService_ReconcilePoolSize_ScalesUpGenericWorkers()
    {
        var config = new CrockConfig
        {
            IdleTimeoutSeconds = 300,
            MaxConcurrency = 3,
            WarmIdleBuffer = 1,
            LocalPort = 9999,
            Model = "gpt-4"
        };
        var (service, _, projectionStore, batchProvider, _) = CreateSystem(config);

        projectionStore.GetDispatchableTaskCountFunc = (now, ct) => Task.FromResult(Result.Ok(2));
        projectionStore.GetActiveWorkersFunc = ct => Task.FromResult(Result.Ok(ImmutableArray<WorkerHandle>.Empty));

        var submittedSpecs = new List<WorkerSpec>();
        batchProvider.SubmitFunc = (idemKey, spec, ct) =>
        {
            submittedSpecs.Add(spec);
            return Task.FromResult(Result.Ok(new WorkerHandle(idemKey, new ProviderBatchRef("batch_gen"))));
        };

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);

        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Desired generic workers = Math.Min(dispatchable (2) + warmIdleBuffer (1), maxConcurrency (3)) = 3.
        // Since active workers = 0, it should scale up by submitting 3 generic workers.
        Assert.Equal(3, submittedSpecs.Count);
        Assert.All(submittedSpecs, spec => Assert.IsType<WorkerSpec.Fresh>(spec));
    }

    [Fact]
    public async Task PoolManagerService_ReconcilePoolSize_SubmitsResumeWorkersForTasksWithCheckpoints()
    {
        var config = new CrockConfig
        {
            IdleTimeoutSeconds = 300,
            MaxConcurrency = 2,
            WarmIdleBuffer = 0,
            LocalPort = 9999,
            Model = "gpt-4"
        };
        var (service, _, projectionStore, batchProvider, _) = CreateSystem(config);

        var task1 = new WorkflowState.Queued(new TaskId("tsk_resume"), new WorkingDir("/tmp"), "Prompt", new Priority(1), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        projectionStore.ListTasksFunc = ct => Task.FromResult(Result.Ok(ImmutableArray.Create<WorkflowState>(task1)));
        projectionStore.GetDispatchableTaskCountFunc = (now, ct) => Task.FromResult(Result.Ok(1));
        projectionStore.GetActiveWorkersFunc = ct => Task.FromResult(Result.Ok(ImmutableArray<WorkerHandle>.Empty));

        // Setup resume data for task1
        (Checkpoint, ImmutableArray<ChildResult>)? resumeData = (new Checkpoint("checkpoint_blob", 1), ImmutableArray.Create(new ChildResult(new TaskId("subtask_1"), new ResultSummary("Child complete"))));
        projectionStore.GetResumeDataFunc = (taskId, ct) => Task.FromResult(Result.Ok(resumeData));

        var submittedSpecs = new List<WorkerSpec>();
        batchProvider.SubmitFunc = (idemKey, spec, ct) =>
        {
            submittedSpecs.Add(spec);
            return Task.FromResult(Result.Ok(new WorkerHandle(idemKey, new ProviderBatchRef("batch_res"))));
        };

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);

        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        Assert.Single(submittedSpecs);
        var spec = submittedSpecs[0];
        Assert.IsType<WorkerSpec.Resume>(spec);
        var resumeSpec = (WorkerSpec.Resume)spec;
        Assert.Equal("checkpoint_blob", resumeSpec.Checkpoint.MessagesBlob);
        Assert.Single(resumeSpec.InjectedResults);
    }
}
