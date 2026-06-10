using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using CrockCode.McpServer;
using Xunit;

namespace CrockCode.Tests;

public class QueueToolsTests : IDisposable
{
    private readonly FakeHttpContextAccessor _httpContextAccessor = new();
    private readonly FakeMcpContextResolver _mcpContextResolver = new();
    private readonly FakeProjectionStore _projectionStore = new();
    private readonly FakeEventStore _eventStore = new();
    private readonly FakeClock _clock = new();
    private readonly FakeRandom _random = new();
    private readonly FakeIdFactory _idFactory = new();
    private readonly WorkflowRunner _runner;
    private readonly QueueTools _queueTools;
    private readonly string _tempWorkingDir;

    public QueueToolsTests()
    {
        _tempWorkingDir = Path.Combine(Path.GetTempPath(), "crock_queue_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkingDir);

        _runner = new WorkflowRunner(_eventStore, _projectionStore, _clock, _random);
        _queueTools = new QueueTools(
            _httpContextAccessor,
            _mcpContextResolver,
            _projectionStore,
            _runner,
            _clock,
            _idFactory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkingDir))
        {
            try
            {
                Directory.Delete(_tempWorkingDir, true);
            }
            catch { }
        }
    }

    private class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private class FakeMcpContextResolver : IMcpContextResolver
    {
        public Result<WorkspaceContext>? Resolved { get; set; }

        public Task<Result<WorkspaceContext>> ResolveContextAsync(HttpContext httpContext, CancellationToken ct = default)
        {
            if (Resolved == null)
            {
                return Task.FromResult<Result<WorkspaceContext>>(new Result<WorkspaceContext>.Err(new Error.Permanent("AUTH_FAILED", "Failed auth")));
            }
            return Task.FromResult(Resolved);
        }
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
        public int TaskCounter { get; set; }

        public TaskId NewTaskId() => new($"tsk_{++TaskCounter}");
        public WorkerId NewWorkerId() => new("wkr_1");
        public CommandId NewCommandId() => new("cmd_1");
        public LeaseRef NewLeaseRef() => new("lease_1");
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
        public List<ToolCallRecord> ToolCalls { get; } = new();
        public bool ShouldFailLoad { get; set; }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default)
        {
            States[taskId] = state;
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => Task.FromResult(Result.Ok(0L));

        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct)
        {
            if (Parents.TryGetValue(taskId, out var pId))
            {
                return Task.FromResult(Result.Ok((TaskId?)pId));
            }
            return Task.FromResult(Result.Ok((TaskId?)null));
        }

        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct)
        {
            if (ShouldFailLoad)
            {
                return Task.FromResult<Result<WorkflowState?>>(new Result<WorkflowState?>.Err(new Error.Permanent("DB_FAIL", "Failed load")));
            }
            States.TryGetValue(taskId, out var state);
            return Task.FromResult(Result.Ok((WorkflowState?)state));
        }

        public Task<Result<ImmutableArray<ToolCallRecord>>> GetToolCallsAsync(TaskId taskId, CancellationToken ct = default)
        {
            return Task.FromResult(Result.Ok(ToolCalls.ToImmutableArray()));
        }

        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkerHandle>>> GetActiveWorkersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<int>> GetDispatchableTaskCountAsync(Instant now, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>> GetResumeDataAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> RecordToolCallAsync(TaskId taskId, string toolName, string argumentsJson, string resultJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<bool>> UpsertWorkerAsync(WorkerId workerId, ProviderBatchRef batchRef, WorkerStatus status, Usage usage, CancellationToken ct = default, string? model = null) => throw new NotImplementedException();
        public Task<Result<string?>> GetWorkerModelAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ProviderBatchRef?>> GetWorkerBatchRefAsync(WorkerId workerId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task get_task_NoHttpContext_ReturnsError()
    {
        // HttpContext is null by default
        var response = await _queueTools.get_task(CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("No active HTTP request", errorProp.GetString());
    }

    [Fact]
    public async Task get_task_ResolveContextFailed_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        _mcpContextResolver.Resolved = null; // Forces auth failed error

        var response = await _queueTools.get_task(CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("Failed auth", errorProp.GetString());
    }

    [Fact]
    public async Task get_task_DatabaseLoadFailed_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));
        _projectionStore.ShouldFailLoad = true;

        var response = await _queueTools.get_task(CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("Failed load", errorProp.GetString());
    }

    [Fact]
    public async Task get_task_NoTaskState_ReturnsNoTask()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        var response = await _queueTools.get_task(CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("status", out var statusProp));
        Assert.Equal("no_task", statusProp.GetString());
    }

    [Fact]
    public async Task get_task_DispatchedTask_TransitionsToRunning_AndIncludesClaudeMd()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        // Write a project CLAUDE.md in the temp workspace
        var claudePath = Path.Combine(_tempWorkingDir, "CLAUDE.md");
        await File.WriteAllTextAsync(claudePath, "My Custom Rules");

        // Seed state in event store & projection store
        var enq = new WorkflowEvent.Enqueued(taskId, new WorkingDir(_tempWorkingDir), "My Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        await _runner.ProcessEventAsync(taskId, enq);
        
        var submit = new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("worker_1"), _clock.Now);
        await _runner.ProcessEventAsync(taskId, submit);

        _projectionStore.States[taskId] = _eventStore.Appended[^1].NextState;

        // Execute get_task
        var response = await _queueTools.get_task(CancellationToken.None);
        var doc = JsonDocument.Parse(response);

        Assert.Equal("running", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(taskId.Value, doc.RootElement.GetProperty("taskId").GetString());
        Assert.Contains("My Prompt", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Contains("=== PROJECT CLAUDE.md ===", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Contains("My Custom Rules", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(_tempWorkingDir, doc.RootElement.GetProperty("workingDir").GetString());

        // Verify it appended a TaskClaimed event to transition to Running
        Assert.Contains(_eventStore.Stream[taskId], e => e is WorkflowEvent.TaskClaimed);
    }

    [Fact]
    public async Task complete_task_AuthFailed_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        _mcpContextResolver.Resolved = null;

        var response = await _queueTools.complete_task("My Summary", "success", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
    }

    [Fact]
    public async Task complete_task_TaskNotFound_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_missing");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        var response = await _queueTools.complete_task("Done changes", "success", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("Task task_missing does not exist", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task complete_task_RunningTask_Succeeds()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        // Enqueue, Submit, Claim to put it in Running
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.Enqueued(taskId, new WorkingDir(_tempWorkingDir), "Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("worker_1"), _clock.Now));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.TaskClaimed(taskId, new WorkerId("worker_1"), new LeaseRef("l1"), _clock.Now));
        _projectionStore.States[taskId] = _eventStore.Appended[^1].NextState;

        var response = await _queueTools.complete_task("My Change summary", "success", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());

        // Verify Event Store has CompletionRequested event
        Assert.Contains(_eventStore.Stream[taskId], e => e is WorkflowEvent.CompletionRequested cr && cr.Status == CompletionStatus.Success && cr.ResultSummary.Summary == "My Change summary");
    }

    [Fact]
    public async Task complete_task_RunningTask_Fails_WhenSpecifyingFailure()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.Enqueued(taskId, new WorkingDir(_tempWorkingDir), "Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("worker_1"), _clock.Now));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.TaskClaimed(taskId, new WorkerId("worker_1"), new LeaseRef("l1"), _clock.Now));
        _projectionStore.States[taskId] = _eventStore.Appended[^1].NextState;

        var response = await _queueTools.complete_task("Failed to run build", "failure", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());

        Assert.Contains(_eventStore.Stream[taskId], e => e is WorkflowEvent.CompletionRequested cr && cr.Status == CompletionStatus.Failure && cr.ResultSummary.Summary == "Failed to run build");
    }

    [Fact]
    public async Task Task_SpawnsSubtask_Successfully()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var parentId = new TaskId("task_parent");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(parentId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_parent")));

        // Setup Parent in Running state
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.Enqueued(parentId, new WorkingDir(_tempWorkingDir), "Parent Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.WorkerSubmitted(parentId, new WorkerId("worker_parent"), _clock.Now));
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.TaskClaimed(parentId, new WorkerId("worker_parent"), new LeaseRef("l_p"), _clock.Now));
        _projectionStore.States[parentId] = _eventStore.Appended[^1].NextState;

        // Run subtask spawn
        var response = await _queueTools.Task("Research DB", "Analyze SqliteStore tests", "researcher", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        
        if (doc.RootElement.TryGetProperty("error", out var errProp))
        {
            Assert.Fail($"Expected taskId but got error: {errProp.GetString()}");
        }
        var spawnedId = doc.RootElement.GetProperty("taskId").GetString();
        Assert.NotNull(spawnedId);
        Assert.Equal("tsk_1", spawnedId);

        // Verify parent gets SubtaskEnqueued, and child gets Enqueued
        Assert.Contains(_eventStore.Stream[parentId], e => e is WorkflowEvent.SubtaskEnqueued se && se.ChildId.Value == "tsk_1");
        Assert.Contains(_eventStore.Stream[new TaskId("tsk_1")], e => e is WorkflowEvent.Enqueued eq && eq.ParentId == parentId && eq.Prompt == "Analyze SqliteStore tests");
    }

    [Fact]
    public async Task Task_GuardDepthLimit_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        
        // Setup a deep chain of parent task relationships
        var tasks = new[] { new TaskId("t0"), new TaskId("t1"), new TaskId("t2"), new TaskId("t3"), new TaskId("t4"), new TaskId("t5") };
        for (int i = 1; i < tasks.Length; i++)
        {
            _projectionStore.Parents[tasks[i]] = tasks[i - 1];
        }

        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(tasks[5], new WorkingDir(_tempWorkingDir), new WorkerId("worker_deep")));

        var response = await _queueTools.Task("Subtask", "Deep prompt", "worker", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("Max subtask depth limit of 5 exceeded.", errorProp.GetString());
    }

    [Fact]
    public async Task Await_SecurityViolation_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var parentId = new TaskId("parent");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(parentId, new WorkingDir(_tempWorkingDir), new WorkerId("worker")));

        // Task to await is NOT child of parent
        var badChildId = new TaskId("some_other_task");
        _projectionStore.Parents[badChildId] = new TaskId("different_parent");

        var response = await _queueTools.@await(new[] { "some_other_task" }, CancellationToken.None);
        var doc = JsonDocument.Parse(response);

        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Contains("Security Violation", errorProp.GetString());
    }

    [Fact]
    public async Task Await_NoHandles_ReturnsError()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var parentId = new TaskId("parent");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(parentId, new WorkingDir(_tempWorkingDir), new WorkerId("worker")));

        var response = await _queueTools.@await(Array.Empty<string>(), CancellationToken.None);
        var doc = JsonDocument.Parse(response);

        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Equal("No task handles specified to await.", errorProp.GetString());
    }

    [Fact]
    public async Task Await_ValidHandles_CreatesCheckpoint_AndSuspends()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var parentId = new TaskId("parent");
        var childId = new TaskId("child");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(parentId, new WorkingDir(_tempWorkingDir), new WorkerId("worker")));

        // 1. Establish parent-child relationship in projection store
        _projectionStore.Parents[childId] = parentId;

        // 2. Put parent into Running
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.Enqueued(parentId, new WorkingDir(_tempWorkingDir), "Parent Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.WorkerSubmitted(parentId, new WorkerId("worker"), _clock.Now));
        await _runner.ProcessEventAsync(parentId, new WorkflowEvent.TaskClaimed(parentId, new WorkerId("worker"), new LeaseRef("l_p"), _clock.Now));
        _projectionStore.States[parentId] = _eventStore.Appended[^1].NextState;

        // 3. Add some tool call history in the projection store for parent
        _projectionStore.ToolCalls.Add(new ToolCallRecord("Read", "{\"file_path\":\"hello.txt\"}", "{\"content\":[{\"text\":\"File content hello\"}]}"));

        // 4. Run await
        var response = await _queueTools.@await(new[] { "child" }, CancellationToken.None);
        var doc = JsonDocument.Parse(response);

        Assert.Equal("suspended", doc.RootElement.GetProperty("status").GetString());

        // Verify AwaitRequested event is in stream and contains messages checkpoint
        var eventStream = _eventStore.Stream[parentId];
        var awaitEvt = (WorkflowEvent.AwaitRequested)eventStream.Find(e => e is WorkflowEvent.AwaitRequested)!;
        Assert.NotNull(awaitEvt);
        Assert.Equal("child", ((AwaitSpec.AwaitTasksSpec)awaitEvt.AwaitSpec).TaskIds[0].Value);
        
        // Assert the checkpoint message list contains the tool call and result reconstructed!
        var messages = JsonDocument.Parse(awaitEvt.Checkpoint.MessagesBlob).RootElement;
        Assert.Equal(3, messages.GetArrayLength()); // user prompt, tool call assistant, tool call user response
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Read", messages[1].GetProperty("content")[0].GetProperty("name").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("File content hello", messages[2].GetProperty("content")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task report_progress_SuccessfullyReports()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        var taskId = new TaskId("task_1");
        _mcpContextResolver.Resolved = Result.Ok(new WorkspaceContext(taskId, new WorkingDir(_tempWorkingDir), new WorkerId("worker_1")));

        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.Enqueued(taskId, new WorkingDir(_tempWorkingDir), "Prompt", new Priority(0), 1, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.WorkerSubmitted(taskId, new WorkerId("worker_1"), _clock.Now));
        await _runner.ProcessEventAsync(taskId, new WorkflowEvent.TaskClaimed(taskId, new WorkerId("worker_1"), new LeaseRef("l1"), _clock.Now));
        _projectionStore.States[taskId] = _eventStore.Appended[^1].NextState;

        var response = await _queueTools.report_progress("Working hard", CancellationToken.None);
        var doc = JsonDocument.Parse(response);
        Assert.Equal("reported", doc.RootElement.GetProperty("status").GetString());

        // Verify ProgressReported event exists
        Assert.Contains(_eventStore.Stream[taskId], e => e is WorkflowEvent.ProgressReported pr && pr.Message == "Working hard");
    }
}
