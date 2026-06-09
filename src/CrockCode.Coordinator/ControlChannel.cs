using System.Collections.Immutable;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;

namespace CrockCode.Coordinator;

public sealed class ControlChannel : IControlChannel
{
    private readonly WorkflowRunner _runner;
    private readonly IProjectionStore _projectionStore;
    private readonly IIdFactory _idFactory;
    private readonly IClock _clock;
    private readonly ITunnelProvider _tunnelProvider;
    private readonly CrockConfig _config;

    public ControlChannel(
        WorkflowRunner runner,
        IProjectionStore projectionStore,
        IIdFactory idFactory,
        IClock clock,
        ITunnelProvider tunnelProvider,
        CrockConfig config)
    {
        _runner = runner;
        _projectionStore = projectionStore;
        _idFactory = idFactory;
        _clock = clock;
        _tunnelProvider = tunnelProvider;
        _config = config;
    }

    public async Task<Result<TaskId>> EnqueueTaskAsync(
        WorkingDir workingDir, string prompt, Priority priority, int maxAttempts,
        ImmutableArray<string> allowedTools, ImmutableArray<string> disallowedTools,
        TaskId? parentId = null,
        CancellationToken ct = default)
    {
        var taskId = _idFactory.NewTaskId();
        var enqueued = new WorkflowEvent.Enqueued(taskId, workingDir, prompt, priority, maxAttempts, allowedTools, disallowedTools, parentId);
        var result = await _runner.ProcessEventAsync(taskId, enqueued, ct);
        return result.Map(_ => taskId);
    }

    public async Task<Result<bool>> CancelTaskAsync(TaskId taskId, CancellationToken ct = default)
    {
        var cancelEvent = new WorkflowEvent.PermanentFailed(
            taskId,
            new Error.Permanent("CANCELLED", "Task cancelled by user"),
            _clock.Now);

        var result = await _runner.ProcessEventAsync(taskId, cancelEvent, ct);
        return result.Map(_ => true);
    }

    public async Task<Result<WorkflowState>> GetTaskStateAsync(TaskId taskId, CancellationToken ct = default)
    {
        var stateResult = await _projectionStore.LoadAsync(taskId, ct);
        if (stateResult is Result<WorkflowState?>.Ok ok)
        {
            if (ok.Value == null)
            {
                return new Result<WorkflowState>.Err(new Error.Permanent("TASK_NOT_FOUND", $"Task {taskId} not found"));
            }
            return Result.Ok(ok.Value);
        }
        return new Result<WorkflowState>.Err(stateResult.UnwrapErr());
    }

    public async Task<Result<System.Collections.Immutable.ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default)
    {
        return await _projectionStore.ListTasksAsync(ct);
    }

    public IAsyncEnumerable<StreamEnvelope> FollowStreamAsync(CancellationToken ct = default)
    {
        throw new NotSupportedException("FollowStreamAsync is not supported on the server-side ControlChannel.");
    }

    public async Task<Result<string>> GetTunnelUrlAsync(CancellationToken ct = default)
    {
        var res = await _tunnelProvider.StartAsync(_config.LocalPort, ct);
        return res.Map(endpoint => endpoint.Url);
    }

    public async Task<Result<bool>> ProbeTunnelAsync(CancellationToken ct = default)
    {
        var res = await _tunnelProvider.StartAsync(_config.LocalPort, ct);
        if (res is Result<PublicEndpoint>.Ok ok)
        {
            return await _tunnelProvider.ProbeAsync(ok.Value, ct);
        }
        return new Result<bool>.Err(res.UnwrapErr());
    }
}
