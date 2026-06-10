using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.McpServer;
using Xunit;

namespace CrockCode.Tests;

public class McpServerTests
{
    private class FakeTokenSigner : ITokenSigner
    {
        public bool ShouldFail { get; set; }
        public WorkspaceContext? ContextToReturn { get; set; }

        public Result<WorkspaceToken> Sign(WorkspaceContext context, TimeSpan validity)
        {
            return Result.Ok(new WorkspaceToken("valid_token"));
        }

        public Result<WorkspaceContext> Verify(WorkspaceToken token)
        {
            if (ShouldFail || ContextToReturn == null)
            {
                return new Result<WorkspaceContext>.Err(new Error.Permanent("INVALID_TOKEN", "Invalid token signature"));
            }
            return Result.Ok(ContextToReturn);
        }
    }

    private class FakeProjectionStore : IProjectionStore
    {
        public WorkflowState? WorkerState { get; set; }

        public Task<Result<WorkflowState?>> LoadByWorkerAsync(WorkerId workerId, CancellationToken ct = default)
        {
            return Task.FromResult(Result.Ok((WorkflowState?)WorkerState));
        }

        public Task<Result<bool>> UpsertAsync(TaskId taskId, WorkflowState state, long version, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<long>> GetVersionAsync(TaskId taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<TaskId?>> GetParentTaskIdAsync(TaskId taskId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<WorkflowState?>> LoadAsync(TaskId taskId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ImmutableArray<TaskId>>> GetActiveTaskIdsAsync(CancellationToken ct = default) => throw new NotImplementedException();
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
    public async Task ResolveContextAsync_MissingAuthorizationHeader_ReturnsUnauthorized()
    {
        var signer = new FakeTokenSigner();
        var store = new FakeProjectionStore();
        var resolver = new McpContextResolver(signer, store);

        var context = new DefaultHttpContext();

        var result = await resolver.ResolveContextAsync(context);

        Assert.True(result.IsErr);
        Assert.Equal("UNAUTHORIZED", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ResolveContextAsync_InvalidAuthorizationHeaderFormat_ReturnsUnauthorized()
    {
        var signer = new FakeTokenSigner();
        var store = new FakeProjectionStore();
        var resolver = new McpContextResolver(signer, store);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Basic dXNlcjpwYXNz";

        var result = await resolver.ResolveContextAsync(context);

        Assert.True(result.IsErr);
        Assert.Equal("UNAUTHORIZED", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ResolveContextAsync_InvalidToken_ReturnsVerificationError()
    {
        var signer = new FakeTokenSigner { ShouldFail = true };
        var store = new FakeProjectionStore();
        var resolver = new McpContextResolver(signer, store);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer badtoken";

        var result = await resolver.ResolveContextAsync(context);

        Assert.True(result.IsErr);
        Assert.Equal("INVALID_TOKEN", result.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ResolveContextAsync_ValidToken_NoActiveRunningTask_ReturnsVerifiedContext()
    {
        var expectedCtx = new WorkspaceContext(new TaskId("task_1"), new WorkingDir("/tmp"), new WorkerId("worker_1"));
        var signer = new FakeTokenSigner { ContextToReturn = expectedCtx };
        var store = new FakeProjectionStore { WorkerState = null };
        var resolver = new McpContextResolver(signer, store);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid_token";

        var result = await resolver.ResolveContextAsync(context);

        Assert.True(result.IsOk);
        var resolvedCtx = result.Unwrap();
        Assert.Equal(expectedCtx.TaskId, resolvedCtx.TaskId);
        Assert.Equal(expectedCtx.WorkingDir, resolvedCtx.WorkingDir);
        Assert.Equal(expectedCtx.WorkerId, resolvedCtx.WorkerId);
    }

    [Fact]
    public async Task ResolveContextAsync_ValidToken_HasActiveRunningTask_ReturnsTaskContext()
    {
        var tokenCtx = new WorkspaceContext(new TaskId("task_old"), new WorkingDir("/tmp/old"), new WorkerId("worker_1"));
        var signer = new FakeTokenSigner { ContextToReturn = tokenCtx };

        var taskState = new WorkflowState.Running(
            new TaskId("task_active"),
            new WorkingDir("/tmp/active"),
            "Some prompt",
            new Priority(0),
            1,
            3,
            new WorkerId("worker_1"),
            new LeaseRef("lease_1"),
            new Instant(DateTimeOffset.UtcNow),
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty
        );
        var store = new FakeProjectionStore { WorkerState = taskState };
        var resolver = new McpContextResolver(signer, store);

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer valid_token";

        var result = await resolver.ResolveContextAsync(context);

        Assert.True(result.IsOk);
        var resolvedCtx = result.Unwrap();
        Assert.Equal(taskState.TaskId, resolvedCtx.TaskId);
        Assert.Equal(taskState.WorkingDir, resolvedCtx.WorkingDir);
        Assert.Equal(tokenCtx.WorkerId, resolvedCtx.WorkerId);
    }

    [Fact]
    public void ToolSchemaRegistry_GetToolsForModel_ReturnsAllTools()
    {
        var tools = ToolSchemaRegistry.GetToolsForModel(null);
        Assert.NotNull(tools);
        Assert.Equal(11, tools.Count);
        
        var toolNames = new HashSet<string>(tools.ConvertAll(t => t.Name));
        Assert.Contains("Read", toolNames);
        Assert.Contains("Write", toolNames);
        Assert.Contains("Edit", toolNames);
        Assert.Contains("Glob", toolNames);
        Assert.Contains("Grep", toolNames);
        Assert.Contains("Bash", toolNames);
        Assert.Contains("BashOutput", toolNames);
        Assert.Contains("get_task", toolNames);
        Assert.Contains("complete_task", toolNames);
        Assert.Contains("Task", toolNames);
        Assert.Contains("await", toolNames);
    }

    [Fact]
    public async Task BackgroundProcessManager_RunsProcess_CapturesOutputAndExits()
    {
        var manager = new BackgroundProcessManager();
        var tempDir = Path.GetTempPath();
        
        var id = manager.Start("echo 'hello world'", tempDir);
        Assert.NotNull(id);
        Assert.StartsWith("bg_task_", id);

        var bgProc = manager.Get(id);
        Assert.NotNull(bgProc);
        Assert.Equal("echo 'hello world'", bgProc.Command);

        var stopwatch = Stopwatch.StartNew();
        while (!bgProc.IsCompleted && stopwatch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50);
        }

        Assert.True(bgProc.IsCompleted, "Background process should complete within timeout.");
        Assert.Equal(0, bgProc.ExitCode);
        
        string output;
        lock (bgProc.Output)
        {
            output = bgProc.Output.ToString();
        }
        Assert.Contains("hello world", output);
        
        var nonExistent = manager.Get("invalid_id");
        Assert.Null(nonExistent);
    }
}
