using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;

namespace CrockCode.Coordinator;

public sealed class PoolManagerService : BackgroundService
{
    private readonly IProjectionStore _projectionStore;
    private readonly IEventStore _eventStore;
    private readonly IBatchProvider _batchProvider;
    private readonly OutboxDispatcher _outboxDispatcher;
    private readonly WorkflowRunner _runner;
    private readonly IClock _clock;
    private readonly ITokenSigner _tokenSigner;
    private readonly IIdFactory _idFactory;
    private readonly ITunnelProvider _tunnelProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PoolManagerService> _logger;

    private readonly CrockConfig _config;
    private DateTime? _idleSince;

    public PoolManagerService(
        IProjectionStore projectionStore,
        IEventStore eventStore,
        IBatchProvider batchProvider,
        OutboxDispatcher outboxDispatcher,
        WorkflowRunner runner,
        IClock clock,
        ITokenSigner tokenSigner,
        IIdFactory idFactory,
        ITunnelProvider tunnelProvider,
        CrockConfig config,
        IHostApplicationLifetime lifetime,
        ILogger<PoolManagerService> logger)
    {
        _projectionStore = projectionStore;
        _eventStore = eventStore;
        _batchProvider = batchProvider;
        _outboxDispatcher = outboxDispatcher;
        _runner = runner;
        _clock = clock;
        _tokenSigner = tokenSigner;
        _idFactory = idFactory;
        _tunnelProvider = tunnelProvider;
        _config = config;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CrockCode Pool Manager Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Dispatch any standalone/task outbox commands
                await _outboxDispatcher.DispatchPendingAsync(stoppingToken);

                // 2. Poll active workers
                await PollActiveWorkersAsync(stoppingToken);

                // 3. Manage the generic pool size
                await ReconcilePoolSizeAsync(stoppingToken);

                // 4. Idle auto-shutdown check
                var activeTasksRes = await _projectionStore.GetActiveTaskIdsAsync(stoppingToken);
                var activeWorkersRes = await _projectionStore.GetActiveWorkersAsync(stoppingToken);

                bool isIdle = (activeTasksRes is Result<ImmutableArray<TaskId>>.Ok tasksOk && tasksOk.Value.Length == 0) &&
                              (activeWorkersRes is Result<ImmutableArray<WorkerHandle>>.Ok workersOk && workersOk.Value.Length == 0);

                if (isIdle)
                {
                    if (_idleSince == null)
                    {
                        _idleSince = DateTime.UtcNow;
                        _logger.LogInformation("Coordinator has become idle. Idle auto-shutdown timer started.");
                    }
                    else
                    {
                        var idleDuration = DateTime.UtcNow - _idleSince.Value;
                        if (idleDuration.TotalSeconds >= _config.IdleTimeoutSeconds)
                        {
                            _logger.LogWarning("Coordinator has been idle for {IdleDuration}s (limit {Limit}s). Shutting down...",
                                idleDuration.TotalSeconds, _config.IdleTimeoutSeconds);
                            _lifetime.StopApplication();
                            break;
                        }
                    }
                }
                else
                {
                    if (_idleSince != null)
                    {
                        _logger.LogInformation("Activity detected. Resetting idle auto-shutdown timer.");
                        _idleSince = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Pool Manager loop");
            }

            // Sleep for 15 seconds
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task PollActiveWorkersAsync(CancellationToken ct)
    {
        var activeWorkersResult = await _projectionStore.GetActiveWorkersAsync(ct);
        if (activeWorkersResult is not Result<ImmutableArray<WorkerHandle>>.Ok ok)
        {
            return;
        }

        foreach (var handle in ok.Value)
        {
            _logger.LogInformation("Polling worker {WorkerId} batch {BatchRef}", handle.Id, handle.BatchRef);
            var pollResult = await _batchProvider.PollAsync(handle, ct);
            if (pollResult is not Result<WorkerOutcome>.Ok pollOk)
            {
                continue;
            }

            var outcome = pollOk.Value;
            if (outcome.Status is WorkerStatus.InFlight or WorkerStatus.Submitted)
            {
                continue;
            }

            _logger.LogInformation("Worker {WorkerId} reached terminal state {State}", handle.Id, outcome.Status.GetType().Name);

            // Find the task assigned to this worker
            var taskResult = await _projectionStore.LoadByWorkerAsync(handle.Id, ct);
            if (taskResult is Result<WorkflowState?>.Ok taskOk && taskOk.Value != null)
            {
                var taskState = taskOk.Value;
                var taskId = taskState.TaskId;

                await outcome.Status.Match(
                    submitted => Task.CompletedTask,
                    inFlight => Task.CompletedTask,
                    async succeeded =>
                    {
                        var evt = new WorkflowEvent.WorkerSettled(taskId, handle.Id, outcome.Usage, _clock.Now);
                        await _runner.ProcessEventAsync(taskId, evt, ct);
                    },
                    async errored =>
                    {
                        var evt = new WorkflowEvent.PermanentFailed(taskId, errored.Error, _clock.Now);
                        await _runner.ProcessEventAsync(taskId, evt, ct);
                    },
                    async expired =>
                    {
                        var evt = new WorkflowEvent.WorkerExpired(taskId, handle.Id, _clock.Now);
                        await _runner.ProcessEventAsync(taskId, evt, ct);
                    }
                );
            }

            // Update worker status in SQLite
            await _projectionStore.UpsertWorkerAsync(handle.Id, handle.BatchRef, outcome.Status, outcome.Usage, ct);
        }
    }

    private async Task ReconcilePoolSizeAsync(CancellationToken ct)
    {
        // Get dispatchable tasks count
        var dispatchableResult = await _projectionStore.GetDispatchableTaskCountAsync(_clock.Now, ct);
        if (dispatchableResult is not Result<int>.Ok dispatchableOk)
        {
            return;
        }
        int dispatchableCount = dispatchableOk.Value;

        // Get active tasks
        var tasksResult = await _projectionStore.ListTasksAsync(ct);
        if (tasksResult is not Result<ImmutableArray<WorkflowState>>.Ok tasksOk)
        {
            return;
        }

        var queuedTasks = tasksOk.Value.OfType<WorkflowState.Queued>().OrderByDescending(t => t.Priority.Value).ToList();

        // Get in-flight workers
        var activeWorkersResult = await _projectionStore.GetActiveWorkersAsync(ct);
        int inFlightCount = activeWorkersResult is Result<ImmutableArray<WorkerHandle>>.Ok activeOk ? activeOk.Value.Length : 0;

        int maxConcurrency = _config.MaxConcurrency;
        int currentInFlight = inFlightCount;

        // 1. First submit specific resume workers for queued tasks with checkpoints
        foreach (var task in queuedTasks)
        {
            if (currentInFlight >= maxConcurrency) break;

            var resumeDataRes = await _projectionStore.GetResumeDataAsync(task.TaskId, ct);
            if (resumeDataRes is Result<(Checkpoint Checkpoint, ImmutableArray<ChildResult> Results)?>.Ok resumeDataOk && resumeDataOk.Value != null)
            {
                var resumeData = resumeDataOk.Value.Value;
                _logger.LogInformation("Task {TaskId} is resuming. Submitting specific resume worker.", task.TaskId);

                var workerId = _idFactory.NewWorkerId();
                var tokenResult = _tokenSigner.Sign(new WorkspaceContext(task.TaskId, task.WorkingDir, workerId), TimeSpan.FromHours(24));
                if (tokenResult is not Result<WorkspaceToken>.Ok tokenOk)
                {
                    continue;
                }

                var tunnelResult = await _tunnelProvider.StartAsync(_config.LocalPort, ct);
                if (tunnelResult is not Result<PublicEndpoint>.Ok tunnelOk)
                {
                    _logger.LogWarning("Unable to resolve public MCP URL. Skipping resume worker submission.");
                    break;
                }

                string mcpUrl = tunnelOk.Value.Url + "/api/mcp";

                var spec = new WorkerSpec.Resume(
                    McpEndpoint: mcpUrl,
                    AuthToken: tokenOk.Value.Value,
                    Model: _config.Model,
                    MaxToolTurns: 50,
                    SystemPrompt: "Work diligently on the task assigned.",
                    Checkpoint: resumeData.Checkpoint,
                    InjectedResults: resumeData.Results
                );

                await _projectionStore.UpsertWorkerAsync(workerId, new ProviderBatchRef(""), new WorkerStatus.Submitted(), Usage.Zero, ct, spec.Model);

                var dispatchEvent = new WorkflowEvent.WorkerSubmitted(task.TaskId, workerId, _clock.Now);
                await _runner.ProcessEventAsync(task.TaskId, dispatchEvent, ct);

                var submitResult = await _batchProvider.SubmitAsync(workerId, spec, ct);
                if (submitResult is Result<WorkerHandle>.Ok submitOk)
                {
                    var handle = submitOk.Value;
                    await _projectionStore.UpsertWorkerAsync(workerId, handle.BatchRef, new WorkerStatus.InFlight(), Usage.Zero, ct);
                    _logger.LogInformation("Successfully submitted resume worker {WorkerId} with batch {BatchRef} for task {TaskId}", workerId, handle.BatchRef, task.TaskId);
                    currentInFlight++;
                }
                else
                {
                    _logger.LogError("Failed to submit resume worker {WorkerId} to provider: {Err}", workerId, submitResult.UnwrapErr());
                    var err = submitResult.UnwrapErr();
                    await _projectionStore.UpsertWorkerAsync(workerId, new ProviderBatchRef(""), new WorkerStatus.Errored(err), Usage.Zero, ct);
                    var failEvent = new WorkflowEvent.PermanentFailed(task.TaskId, err, _clock.Now);
                    await _runner.ProcessEventAsync(task.TaskId, failEvent, ct);
                }
            }
        }

        // 2. Now reconcile generic pool size for standard/fresh queued tasks
        int desired = Math.Min(dispatchableCount + _config.WarmIdleBuffer, maxConcurrency);
        if (desired > currentInFlight)
        {
            int submitCount = desired - currentInFlight;
            _logger.LogInformation("Scaling pool up: desired={Desired}, inFlight={InFlight}. Submitting {Count} new generic workers.", desired, currentInFlight, submitCount);

            var tunnelResult = await _tunnelProvider.StartAsync(_config.LocalPort, ct);
            if (tunnelResult is not Result<PublicEndpoint>.Ok tunnelOk)
            {
                _logger.LogWarning("Unable to resolve public MCP URL. Skipping generic worker submissions.");
                return;
            }

            string mcpUrl = tunnelOk.Value.Url + "/api/mcp";

            for (int i = 0; i < submitCount; i++)
            {
                var workerId = _idFactory.NewWorkerId();
                var tokenResult = _tokenSigner.Sign(new WorkspaceContext(new TaskId(""), new WorkingDir(""), workerId), TimeSpan.FromHours(24));
                if (tokenResult is not Result<WorkspaceToken>.Ok tokenOk)
                {
                    continue;
                }

                var spec = new WorkerSpec.Fresh(
                    McpEndpoint: mcpUrl,
                    AuthToken: tokenOk.Value.Value,
                    Model: _config.Model,
                    MaxToolTurns: 50,
                    SystemPrompt: "Work diligently on the task assigned."
                );

                await _projectionStore.UpsertWorkerAsync(workerId, new ProviderBatchRef(""), new WorkerStatus.Submitted(), Usage.Zero, ct, spec.Model);

                var submitResult = await _batchProvider.SubmitAsync(workerId, spec, ct);
                if (submitResult is Result<WorkerHandle>.Ok submitOk)
                {
                    var handle = submitOk.Value;
                    await _projectionStore.UpsertWorkerAsync(workerId, handle.BatchRef, new WorkerStatus.InFlight(), Usage.Zero, ct);
                    _logger.LogInformation("Successfully submitted generic worker {WorkerId} with batch {BatchRef}", workerId, handle.BatchRef);
                    currentInFlight++;
                }
                else
                {
                    _logger.LogError("Failed to submit generic worker {WorkerId} to provider: {Err}", workerId, submitResult.UnwrapErr());
                    var err = submitResult.UnwrapErr();
                    await _projectionStore.UpsertWorkerAsync(workerId, new ProviderBatchRef(""), new WorkerStatus.Errored(err), Usage.Zero, ct);
                }
            }
        }
    }
}
