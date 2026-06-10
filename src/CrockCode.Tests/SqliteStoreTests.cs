using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Storage;
using Xunit;

namespace CrockCode.Tests;

public class SqliteStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly FakeClock _clock = new();
    private readonly SqliteStore _store;

    public SqliteStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crock_sqlite_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "crock.db");
        _store = new SqliteStore(_dbPath, _clock);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private class FakeClock : IClock
    {
        public Instant Now { get; set; } = new(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Database_ShouldInitialize_TablesSuccessfully()
    {
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task EventStore_Should_AppendAndLoadEvents()
    {
        var taskId = new TaskId("tsk_db_1");
        var workingDir = new WorkingDir("/tmp");
        var prompt = "DB Prompt";
        
        var enqEvent = new WorkflowEvent.Enqueued(taskId, workingDir, prompt, new Priority(5), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        var state = new WorkflowState.Queued(taskId, workingDir, prompt, new Priority(5), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        
        var batch = new TransitionBatch(
            taskId,
            ExpectedVersion: 0,
            NextState: state,
            Events: ImmutableArray.Create<WorkflowEvent>(enqEvent),
            Commands: ImmutableArray<Command>.Empty
        );

        var appendResult = await _store.AppendAsync(batch);
        Assert.True(appendResult.IsOk);
        Assert.Equal(1, appendResult.Unwrap());

        var versionRes = await _store.GetVersionAsync(taskId);
        Assert.True(versionRes.IsOk);
        Assert.Equal(1, versionRes.Unwrap());

        var loadRes = await _store.LoadEventsAsync(taskId, 0);
        Assert.True(loadRes.IsOk);
        var events = loadRes.Unwrap();
        Assert.Single(events);
        Assert.IsType<WorkflowEvent.Enqueued>(events[0]);
    }

    [Fact]
    public async Task ProjectionStore_Should_UpsertAndLoadProjections()
    {
        var taskId = new TaskId("tsk_db_2");
        var workingDir = new WorkingDir("/tmp");
        var state = new WorkflowState.Queued(taskId, workingDir, "Task 2", new Priority(0), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var upsertResult = await _store.UpsertAsync(taskId, state, 1);
        Assert.True(upsertResult.IsOk);
        Assert.True(upsertResult.Unwrap());

        var loadResult = await _store.LoadAsync(taskId);
        Assert.True(loadResult.IsOk);
        Assert.NotNull(loadResult.Unwrap());
        Assert.IsType<WorkflowState.Queued>(loadResult.Unwrap());
        Assert.Equal(taskId, loadResult.Unwrap()!.TaskId);
    }

    [Fact]
    public async Task LeaseManager_Should_AcquireAndReleaseLeases()
    {
        var workingDir = new WorkingDir("/tmp/lockdir");
        var taskId1 = new TaskId("tsk_owner_1");
        var taskId2 = new TaskId("tsk_owner_2");

        // 1. First task tree acquires lease
        var res1 = await _store.AcquireAsync(workingDir, taskId1, TimeSpan.FromSeconds(10));
        Assert.True(res1.IsOk);
        
        LeaseRef leaseRef = default;
        var isAcquired = res1.Unwrap().Match(
            acquired => { leaseRef = acquired.Lease; return true; },
            joined => false,
            blocked => false
        );
        Assert.True(isAcquired);
        Assert.NotEqual(default, leaseRef);

        // 2. Second task tree tries to acquire and gets blocked
        var res2 = await _store.AcquireAsync(workingDir, taskId2, TimeSpan.FromSeconds(10));
        Assert.True(res2.IsOk);
        var isBlocked = res2.Unwrap().Match(
            acquired => false,
            joined => false,
            blocked => true
        );
        Assert.True(isBlocked);

        // 3. First task tree releases lease
        var releaseRes = await _store.ReleaseAsync(leaseRef);
        Assert.True(releaseRes.IsOk);

        // 4. Second task tree can now acquire lease
        var res3 = await _store.AcquireAsync(workingDir, taskId2, TimeSpan.FromSeconds(10));
        Assert.True(res3.IsOk);
        var isAcquiredNow = res3.Unwrap().Match(
            acquired => true,
            joined => false,
            blocked => false
        );
        Assert.True(isAcquiredNow);
    }

    [Fact]
    public async Task Outbox_Should_EnqueueDequeueAndAcknowledge()
    {
        var requeueCmd = new Command.Requeue(new TaskId("tsk_outbox"));
        var commands = ImmutableArray.Create<Command>(requeueCmd);

        // Enqueue
        var enqRes = await _store.EnqueueAsync(commands);
        Assert.True(enqRes.IsOk);
        Assert.True(enqRes.Unwrap());

        // Dequeue
        var deqRes = await _store.DequeueAsync(10);
        Assert.True(deqRes.IsOk);
        var entries = deqRes.Unwrap();
        Assert.Single(entries);
        Assert.IsType<Command.Requeue>(entries[0].Command);

        // Acknowledge
        var ackRes = await _store.AcknowledgeAsync(ImmutableArray.Create(entries[0].Id));
        Assert.True(ackRes.IsOk);
        Assert.True(ackRes.Unwrap());

        // Dequeue again should be empty
        var deqRes2 = await _store.DequeueAsync(10);
        Assert.True(deqRes2.IsOk);
        Assert.Empty(deqRes2.Unwrap());
    }

    [Fact]
    public async Task JobStore_Should_ScheduleFetchCompleteFailAndCancel()
    {
        var job = new JobDescriptor(
            JobId: "job_123",
            JobType: "retry",
            TaskId: new TaskId("tsk_job"),
            ScheduledAt: _clock.Now,
            MaxRetries: 3,
            Attempt: 1
        );

        // Schedule
        var schedRes = await _store.ScheduleAsync(job);
        Assert.True(schedRes.IsOk);
        Assert.True(schedRes.Unwrap());

        // Fetch due
        var dueRes = await _store.FetchDueAsync(_clock.Now + TimeSpan.FromSeconds(1), 10);
        Assert.True(dueRes.IsOk);
        var dueJobs = dueRes.Unwrap();
        Assert.Single(dueJobs);
        Assert.Equal("job_123", dueJobs[0].JobId);

        // Fail
        var failRes = await _store.FailAsync("job_123", new Error.Transient("TEST", "retry fail"));
        Assert.True(failRes.IsOk);
        Assert.True(failRes.Unwrap());

        // Complete
        var compRes = await _store.CompleteAsync("job_123");
        Assert.True(compRes.IsOk);
        Assert.True(compRes.Unwrap());

        // Cancel on a different scheduled job
        var job2 = new JobDescriptor(
            JobId: "job_456",
            JobType: "timeout",
            TaskId: new TaskId("tsk_job"),
            ScheduledAt: _clock.Now,
            MaxRetries: 3,
            Attempt: 1
        );
        await _store.ScheduleAsync(job2);
        
        var cancelRes = await _store.CancelAsync("job_456");
        Assert.True(cancelRes.IsOk);
        Assert.True(cancelRes.Unwrap());
    }

    [Fact]
    public async Task ProjectionStore_Should_GetActiveTaskIds_GetDispatchableCount_AndListTasks()
    {
        var taskId = new TaskId("tsk_db_active");
        var workingDir = new WorkingDir("/tmp/active");
        var state = new WorkflowState.Queued(taskId, workingDir, "Active task", new Priority(3), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        // 1. Initially active list should contain it after we upsert
        var upsertResult = await _store.UpsertAsync(taskId, state, 1);
        Assert.True(upsertResult.IsOk);

        var activeIdsResult = await _store.GetActiveTaskIdsAsync();
        Assert.True(activeIdsResult.IsOk);
        Assert.Contains(taskId, activeIdsResult.Unwrap());

        // 2. Dispatchable count should be 1 since it is queued and has no lease
        var dispatchableResult = await _store.GetDispatchableTaskCountAsync(_clock.Now);
        Assert.True(dispatchableResult.IsOk);
        Assert.Equal(1, dispatchableResult.Unwrap());

        // 3. List tasks should return the task state
        var listResult = await _store.ListTasksAsync();
        Assert.True(listResult.IsOk);
        Assert.Contains(listResult.Unwrap(), t => t.TaskId == taskId);
    }

    [Fact]
    public async Task WorkerStore_Should_UpsertLoadAndGetActiveWorkers()
    {
        var workerId = new WorkerId("wkr_db_1");
        var batchRef = new ProviderBatchRef("batch_db_123");
        var status = new WorkerStatus.Submitted();
        var usage = new Usage(120, 60, 0.0012m);

        // 1. Upsert worker
        var upsertRes = await _store.UpsertWorkerAsync(workerId, batchRef, status, usage, CancellationToken.None, "gpt-4");
        Assert.True(upsertRes.IsOk);
        Assert.True(upsertRes.Unwrap());

        // 2. Get active workers should find the worker
        var activeRes = await _store.GetActiveWorkersAsync();
        Assert.True(activeRes.IsOk);
        Assert.Contains(activeRes.Unwrap(), w => w.Id == workerId && w.BatchRef == batchRef);

        // 3. Get worker model
        var modelRes = await _store.GetWorkerModelAsync(workerId);
        Assert.True(modelRes.IsOk);
        Assert.Equal("gpt-4", modelRes.Unwrap());

        // 4. Get worker batch ref
        var batchRes = await _store.GetWorkerBatchRefAsync(workerId);
        Assert.True(batchRes.IsOk);
        Assert.Equal(batchRef, batchRes.Unwrap());
    }

    [Fact]
    public async Task WorkerStore_Should_LoadByWorker()
    {
        var taskId = new TaskId("tsk_db_worker");
        var workerId = new WorkerId("wkr_assigned_1");
        var workingDir = new WorkingDir("/tmp/worker");

        var state = new WorkflowState.Running(
            taskId,
            workingDir,
            "assigned prompt",
            new Priority(1),
            1,
            3,
            workerId,
            new LeaseRef("l_w_1"),
            _clock.Now,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty
        );

        var upsertResult = await _store.UpsertAsync(taskId, state, 1);
        Assert.True(upsertResult.IsOk);

        var loadRes = await _store.LoadByWorkerAsync(workerId);
        Assert.True(loadRes.IsOk);
        Assert.NotNull(loadRes.Unwrap());
        Assert.Equal(taskId, loadRes.Unwrap()!.TaskId);
    }

    [Fact]
    public async Task LeaseManager_Should_RenewAndCheckHeld()
    {
        var workingDir = new WorkingDir("/tmp/lockdir_renew");
        var taskId = new TaskId("tsk_owner_renew");

        var acquireRes = await _store.AcquireAsync(workingDir, taskId, TimeSpan.FromSeconds(5));
        Assert.True(acquireRes.IsOk);

        LeaseRef leaseRef = default;
        acquireRes.Unwrap().Match(
            acquired => { leaseRef = acquired.Lease; return true; },
            joined => false,
            blocked => false
        );

        // 1. Verify it is held
        var heldRes = await _store.IsHeldAsync(leaseRef);
        Assert.True(heldRes.IsOk);
        Assert.True(heldRes.Unwrap());

        // 2. Renew it
        var renewRes = await _store.RenewAsync(leaseRef, TimeSpan.FromSeconds(20));
        Assert.True(renewRes.IsOk);
        Assert.True(renewRes.Unwrap());
    }

    [Fact]
    public async Task ToolCalls_Should_RecordAndGet()
    {
        var taskId = new TaskId("tsk_db_tools");

        // Record
        var recordRes = await _store.RecordToolCallAsync(taskId, "Write", "{\"path\":\"a.txt\"}", "{\"success\":true}");
        Assert.True(recordRes.IsOk);
        Assert.True(recordRes.Unwrap());

        // Get
        var getRes = await _store.GetToolCallsAsync(taskId);
        Assert.True(getRes.IsOk);
        var calls = getRes.Unwrap();
        Assert.Single(calls);
        Assert.Equal("Write", calls[0].ToolName);
        Assert.Equal("{\"path\":\"a.txt\"}", calls[0].ArgumentsJson);
        Assert.Equal("{\"success\":true}", calls[0].ResultJson);
    }

    [Fact]
    public async Task EventStore_Should_GetParentTaskId_AndResumeData()
    {
        var taskId = new TaskId("tsk_db_resume");
        var parentId = new TaskId("tsk_db_parent");
        var workingDir = new WorkingDir("/tmp/resume");

        // 1. Get parent task ID when not present (should be null)
        var parentRes1 = await _store.GetParentTaskIdAsync(taskId);
        Assert.True(parentRes1.IsOk);
        Assert.Null(parentRes1.Unwrap());

        // 2. Get resume data when not present (should be null)
        var resumeRes1 = await _store.GetResumeDataAsync(taskId);
        Assert.True(resumeRes1.IsOk);
        Assert.Null(resumeRes1.Unwrap());

        // 3. Process Enqueued event with ParentId to associate it
        var parentState = new WorkflowState.Queued(parentId, workingDir, "Parent prompt", new Priority(0), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        var parentBatch = new TransitionBatch(
            parentId,
            ExpectedVersion: 0,
            NextState: parentState,
            Events: ImmutableArray.Create<WorkflowEvent>(new WorkflowEvent.Enqueued(parentId, workingDir, "Parent prompt", new Priority(0), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)),
            Commands: ImmutableArray<Command>.Empty
        );
        await _store.AppendAsync(parentBatch);

        var childState = new WorkflowState.Queued(taskId, workingDir, "Child prompt", new Priority(0), 1, 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        var childBatch = new TransitionBatch(
            taskId,
            ExpectedVersion: 0,
            NextState: childState,
            Events: ImmutableArray.Create<WorkflowEvent>(new WorkflowEvent.Enqueued(taskId, workingDir, "Child prompt", new Priority(0), 3, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ParentId: parentId)),
            Commands: ImmutableArray<Command>.Empty
        );
        await _store.AppendAsync(childBatch);

        // Verify parent ID is returned
        var parentRes2 = await _store.GetParentTaskIdAsync(taskId);
        Assert.True(parentRes2.IsOk);
        Assert.Equal(parentId, parentRes2.Unwrap());

        // 4. Append AwaitRequested event
        var checkpoint = new Checkpoint("message_blob", 42);
        var awaitSpec = new AwaitSpec.AwaitTasksSpec(ImmutableArray.Create(new TaskId("subtask_1")));
        var awaitReqEvent = new WorkflowEvent.AwaitRequested(taskId, awaitSpec, checkpoint, _clock.Now);
        
        var runningState = new WorkflowState.Running(
            taskId, workingDir, "Child prompt", new Priority(0), 1, 3,
            new WorkerId("wkr_1"), new LeaseRef("lease_1"), _clock.Now,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );
        var suspendedState = new WorkflowState.Suspended(
            taskId, workingDir, "Child prompt", new Priority(0), 1, 3,
            new WorkerId("wkr_1"), new LeaseRef("lease_1"), _clock.Now,
            awaitSpec, checkpoint, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        // To append AwaitRequested, expected version should be 1 since Enqueued was first
        var awaitReqBatch = new TransitionBatch(
            taskId,
            ExpectedVersion: 1,
            NextState: suspendedState,
            Events: ImmutableArray.Create<WorkflowEvent>(awaitReqEvent),
            Commands: ImmutableArray<Command>.Empty
        );
        await _store.AppendAsync(awaitReqBatch);

        // Verify resume data contains checkpoint but empty results
        var resumeRes2 = await _store.GetResumeDataAsync(taskId);
        Assert.True(resumeRes2.IsOk);
        Assert.NotNull(resumeRes2.Unwrap());
        var data2 = resumeRes2.Unwrap()!.Value;
        Assert.Equal(checkpoint, data2.Checkpoint);
        Assert.Empty(data2.Results);

        // 5. Append AwaitResolved event
        var childResults = ImmutableArray.Create(new ChildResult(new TaskId("subtask_1"), new ResultSummary("Done")));
        var awaitResEvent = new WorkflowEvent.AwaitResolved(taskId, childResults, _clock.Now);
        var awaitResBatch = new TransitionBatch(
            taskId,
            ExpectedVersion: 2,
            NextState: runningState,
            Events: ImmutableArray.Create<WorkflowEvent>(awaitResEvent),
            Commands: ImmutableArray<Command>.Empty
        );
        await _store.AppendAsync(awaitResBatch);

        // Verify resume data contains both checkpoint and results
        var resumeRes3 = await _store.GetResumeDataAsync(taskId);
        Assert.True(resumeRes3.IsOk);
        Assert.NotNull(resumeRes3.Unwrap());
        var data3 = resumeRes3.Unwrap()!.Value;
        Assert.Equal(checkpoint, data3.Checkpoint);
        Assert.Single(data3.Results);
        Assert.Equal("Done", data3.Results[0].ResultSummary.Summary);
    }
}

