using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;

namespace CrockCode.McpServer;

[McpServerToolType]
public sealed class QueueTools
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMcpContextResolver _mcpContextResolver;
    private readonly IProjectionStore _projectionStore;
    private readonly WorkflowRunner _runner;
    private readonly IClock _clock;
    private readonly IIdFactory _idFactory;

    public QueueTools(
        IHttpContextAccessor httpContextAccessor,
        IMcpContextResolver mcpContextResolver,
        IProjectionStore projectionStore,
        WorkflowRunner runner,
        IClock clock,
        IIdFactory idFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _mcpContextResolver = mcpContextResolver;
        _projectionStore = projectionStore;
        _runner = runner;
        _clock = clock;
        _idFactory = idFactory;
    }

    private async Task<Result<WorkspaceContext>> GetContextAsync(CancellationToken ct)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return new Result<WorkspaceContext>.Err(new Error.Permanent("NO_HTTP_CONTEXT", "No active HTTP request"));
        }
        return await _mcpContextResolver.ResolveContextAsync(httpContext, ct);
    }

    [McpServerTool]
    [Description("Get the task details and working directory assigned to this worker. Call this first.")]
    public async Task<string> get_task(CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err)
        {
            return JsonSerializer.Serialize(new { error = err.Error.Match(t => t.Detail, p => p.Detail) });
        }

        var ctx = ctxResult.Unwrap();

        var stateResult = await _projectionStore.LoadAsync(ctx.TaskId, ct);
        if (stateResult is Result<WorkflowState?>.Err stateErr)
        {
            return JsonSerializer.Serialize(new { error = stateErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        var state = stateResult.Unwrap();
        if (state == null)
        {
            return JsonSerializer.Serialize(new { status = "no_task" });
        }

        string prompt = "";
        state.Match(
            queued => { prompt = queued.Prompt; return Unit.Value; },
            disp => { prompt = disp.Prompt; return Unit.Value; },
            run => { prompt = run.Prompt; return Unit.Value; },
            awaiting => { return Unit.Value; },
            comp => { return Unit.Value; },
            failed => { return Unit.Value; },
            suspended => { prompt = suspended.Prompt; return Unit.Value; },
            retrying => { prompt = retrying.Prompt; return Unit.Value; },
            cancelled => { return Unit.Value; }
        );

        if (state is WorkflowState.Dispatched)
        {
            // Transition from Dispatched to Running
            var claimResult = await _runner.ProcessEventAsync(
                ctx.TaskId,
                new WorkflowEvent.TaskClaimed(ctx.TaskId, ctx.WorkerId, new LeaseRef(ctx.TaskId.Value), _clock.Now),
                ct);

            if (claimResult is Result<Unit>.Err claimErr)
            {
                return JsonSerializer.Serialize(new { error = claimErr.Error.Match(t => t.Detail, p => p.Detail) });
            }
        }

        var claudeMd = await LoadClaudeMdInstructionsAsync(ctx.WorkingDir.Value, ct);
        if (!string.IsNullOrEmpty(claudeMd))
        {
            prompt = prompt + "\n\n" + claudeMd;
        }

        return JsonSerializer.Serialize(new {
            status = "running",
            taskId = ctx.TaskId.Value,
            prompt = prompt,
            workingDir = ctx.WorkingDir.Value
        });
    }

    private async Task<string> LoadClaudeMdInstructionsAsync(string workingDir, CancellationToken ct)
    {
        var instructions = new System.Text.StringBuilder();

        // 1. User/Global CLAUDE.md
        try
        {
            var userClaudePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode", "CLAUDE.md");
            if (File.Exists(userClaudePath))
            {
                var content = await File.ReadAllTextAsync(userClaudePath, ct);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    instructions.AppendLine("=== USER CLAUDE.md ===");
                    instructions.AppendLine(content);
                }
            }
        }
        catch
        {
            // Best effort
        }

        // 2. Project CLAUDE.md
        try
        {
            var projectClaudePath = Path.Combine(Path.GetFullPath(workingDir), "CLAUDE.md");
            if (File.Exists(projectClaudePath))
            {
                var content = await File.ReadAllTextAsync(projectClaudePath, ct);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    instructions.AppendLine("=== PROJECT CLAUDE.md ===");
                    instructions.AppendLine(content);
                }
            }
        }
        catch
        {
            // Best effort
        }

        return instructions.ToString();
    }

    [McpServerTool]
    [Description("Mark the assigned task as complete and report results.")]
    public async Task<string> complete_task(
        [Description("The summary of changes made and results.")] string summary,
        [Description("Optional. Success or Failure. Defaults to Success.")] string status,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err)
        {
            return JsonSerializer.Serialize(new { error = err.Error.Match(t => t.Detail, p => p.Detail) });
        }

        var ctx = ctxResult.Unwrap();

        var completionStatus = CompletionStatus.Success;
        if (string.Equals(status, "failure", StringComparison.OrdinalIgnoreCase))
        {
            completionStatus = CompletionStatus.Failure;
        }

        var reqEvent = new WorkflowEvent.CompletionRequested(
            ctx.TaskId,
            new ResultSummary(summary),
            completionStatus,
            _clock.Now);

        var processResult = await _runner.ProcessEventAsync(ctx.TaskId, reqEvent, ct);
        if (processResult is Result<Unit>.Err pErr)
        {
            return JsonSerializer.Serialize(new { error = pErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        return JsonSerializer.Serialize(new { status = "completed" });
    }

    [McpServerTool]
    [Description("Enqueue a child/subagent task relative to the current working directory.")]
    public async Task<string> Task(
        [Description("A brief description of what this subagent will do.")] string description,
        [Description("The detailed prompt/task instructions for the subagent.")] string prompt,
        [Description("The subagent type (e.g. 'codebase_researcher', 'core_implementer').")] string subagent_type,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err)
        {
            return JsonSerializer.Serialize(new { error = err.Error.Match(t => t.Detail, p => p.Detail) });
        }
        var ctx = ctxResult.Unwrap();

        // 1. Guard depth and detect cycles
        int depth = 0;
        TaskId? currentId = ctx.TaskId;
        while (currentId.HasValue)
        {
            depth++;
            if (depth > 5)
            {
                return JsonSerializer.Serialize(new { error = "Max subtask depth limit of 5 exceeded." });
            }
            var parentRes = await _projectionStore.GetParentTaskIdAsync(currentId.Value, ct);
            if (parentRes is Result<TaskId?>.Ok parentOk)
            {
                currentId = parentOk.Value;
            }
            else
            {
                break;
            }
        }

        // 2. Resolve allowed and disallowed tools from parent task state
        var parentStateResult = await _projectionStore.LoadAsync(ctx.TaskId, ct);
        var allowedTools = System.Collections.Immutable.ImmutableArray<string>.Empty;
        var disallowedTools = System.Collections.Immutable.ImmutableArray<string>.Empty;
        if (parentStateResult is Result<WorkflowState?>.Ok parentOkState && parentOkState.Value != null)
        {
            allowedTools = parentOkState.Value.AllowedTools;
            disallowedTools = parentOkState.Value.DisallowedTools;
        }

        var childTaskId = _idFactory.NewTaskId();

        // 3. Post SubtaskEnqueued to parent task
        var subtaskEvent = new WorkflowEvent.SubtaskEnqueued(ctx.TaskId, childTaskId, prompt, new Priority(0));
        var subtaskResult = await _runner.ProcessEventAsync(ctx.TaskId, subtaskEvent, ct);
        if (subtaskResult is Result<Unit>.Err subtaskErr)
        {
            return JsonSerializer.Serialize(new { error = subtaskErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        // 4. Enqueue the child task itself
        var childEnqueued = new WorkflowEvent.Enqueued(
            childTaskId,
            ctx.WorkingDir,
            prompt,
            new Priority(0),
            MaxAttempts: 3,
            allowedTools,
            disallowedTools,
            ParentId: ctx.TaskId
        );
        var childResult = await _runner.ProcessEventAsync(childTaskId, childEnqueued, ct);
        if (childResult is Result<Unit>.Err childErr)
        {
            return JsonSerializer.Serialize(new { error = childErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        return JsonSerializer.Serialize(new { taskId = childTaskId.Value });
    }

    [McpServerTool]
    [Description("Suspend execution and wait for the specified child tasks to reach terminal states.")]
    public async Task<string> @await(
        [Description("Array of task IDs to wait for.")] string[] handles,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err)
        {
            return JsonSerializer.Serialize(new { error = err.Error.Match(t => t.Detail, p => p.Detail) });
        }
        var ctx = ctxResult.Unwrap();

        if (handles == null || handles.Length == 0)
        {
            return JsonSerializer.Serialize(new { error = "No task handles specified to await." });
        }

        // Validate all handles are children of this parent task
        foreach (var handle in handles)
        {
            var childId = new TaskId(handle);
            var parentRes = await _projectionStore.GetParentTaskIdAsync(childId, ct);
            if (parentRes is not Result<TaskId?>.Ok parentOk || parentOk.Value?.Value != ctx.TaskId.Value)
            {
                return JsonSerializer.Serialize(new { error = $"Security Violation: Task {handle} is not a child of the current task." });
            }
        }

        // Construct Checkpoint MessagesBlob
        var messagesList = new List<object>();
        messagesList.Add(new
        {
            role = "user",
            content = "Please fetch the task from MCP and execute it."
        });

        var toolCallsResult = await _projectionStore.GetToolCallsAsync(ctx.TaskId, ct);
        if (toolCallsResult is Result<System.Collections.Immutable.ImmutableArray<ToolCallRecord>>.Ok tcOk)
        {
            foreach (var record in tcOk.Value)
            {
                string toolUseId = "toolu_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                messagesList.Add(new
                {
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "tool_use",
                            id = toolUseId,
                            name = record.ToolName,
                            input = JsonSerializer.Deserialize<JsonElement>(record.ArgumentsJson)
                        }
                    }
                });

                object resultBlock;
                try
                {
                    using var doc = JsonDocument.Parse(record.ResultJson);
                    if (doc.RootElement.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
                    {
                        var textParts = new List<string>();
                        foreach (var block in contentProp.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var textProp))
                            {
                                textParts.Add(textProp.GetString() ?? "");
                            }
                        }
                        string combinedText = string.Join("\n", textParts);
                        resultBlock = new
                        {
                            role = "user",
                            content = new[]
                            {
                                new
                                {
                                    type = "tool_result",
                                    tool_use_id = toolUseId,
                                    content = combinedText
                                }
                            }
                        };
                    }
                    else
                    {
                        resultBlock = new
                        {
                            role = "user",
                            content = new[]
                            {
                                new
                                {
                                    type = "tool_result",
                                    tool_use_id = toolUseId,
                                    content = record.ResultJson
                                }
                            }
                        };
                    }
                }
                catch
                {
                    resultBlock = new
                    {
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "tool_result",
                                tool_use_id = toolUseId,
                                content = record.ResultJson
                            }
                        }
                    };
                }

                messagesList.Add(resultBlock);
            }
        }

        string messagesBlob = JsonSerializer.Serialize(messagesList);
        var checkpoint = new Checkpoint(messagesBlob, 0);

        var taskIds = System.Collections.Immutable.ImmutableArray.CreateRange(handles.Select(h => new TaskId(h)));
        var awaitSpec = new AwaitSpec.AwaitTasksSpec(taskIds);

        var awaitEvent = new WorkflowEvent.AwaitRequested(ctx.TaskId, awaitSpec, checkpoint, _clock.Now);
        var awaitResult = await _runner.ProcessEventAsync(ctx.TaskId, awaitEvent, ct);
        if (awaitResult is Result<Unit>.Err awaitErr)
        {
            return JsonSerializer.Serialize(new { error = awaitErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        return JsonSerializer.Serialize(new { status = "suspended" });
    }

    [McpServerTool]
    [Description("Report the current progress of the task back to the user/daemon.")]
    public async Task<string> report_progress(
        [Description("The progress message.")] string message,
        CancellationToken ct)
    {
        var ctxResult = await GetContextAsync(ct);
        if (ctxResult is Result<WorkspaceContext>.Err err)
        {
            return JsonSerializer.Serialize(new { error = err.Error.Match(t => t.Detail, p => p.Detail) });
        }
        var ctx = ctxResult.Unwrap();

        var progressEvent = new WorkflowEvent.ProgressReported(ctx.TaskId, message, _clock.Now);
        var res = await _runner.ProcessEventAsync(ctx.TaskId, progressEvent, ct);
        if (res is Result<Unit>.Err pErr)
        {
            return JsonSerializer.Serialize(new { error = pErr.Error.Match(t => t.Detail, p => p.Detail) });
        }

        return JsonSerializer.Serialize(new { status = "reported" });
    }
}
