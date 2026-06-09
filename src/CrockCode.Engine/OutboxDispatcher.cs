using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Engine;

public sealed class OutboxDispatcher
{
    private readonly IOutbox _outbox;
    private readonly IBatchProvider _batchProvider;
    private readonly IStreamEventPublisher _streamPublisher;
    private readonly IProjectionStore _projectionStore;
    private readonly ILeaseManager _leaseManager;
    private readonly Func<WorkflowRunner> _runnerFactory;
    private readonly IClock _clock;

    public OutboxDispatcher(
        IOutbox outbox,
        IBatchProvider batchProvider,
        IStreamEventPublisher streamPublisher,
        IProjectionStore projectionStore,
        ILeaseManager leaseManager,
        Func<WorkflowRunner> runnerFactory,
        IClock clock)
    {
        _outbox = outbox;
        _batchProvider = batchProvider;
        _streamPublisher = streamPublisher;
        _projectionStore = projectionStore;
        _leaseManager = leaseManager;
        _runnerFactory = runnerFactory;
        _clock = clock;
    }

    public async Task DispatchPendingAsync(CancellationToken ct = default)
    {
        var dequeueResult = await _outbox.DequeueAsync(10, ct);
        if (dequeueResult is not Result<ImmutableArray<OutboxEntry>>.Ok ok)
        {
            return;
        }

        foreach (var entry in ok.Value)
        {
            var result = await DispatchEntryAsync(entry, ct);
            if (result.IsOk)
            {
                await _outbox.AcknowledgeAsync(ImmutableArray.Create(entry.Id), ct);
            }
            else
            {
                var error = result.UnwrapErr();
                await error.Match(
                    async transient =>
                    {
                        // Transient error: do not acknowledge.
                        // It will remain in the outbox to be retried on the next tick/poll.
                        await Task.CompletedTask;
                        return Unit.Value;
                    },
                    async permanent =>
                    {
                        // Permanent error: report to the workflow engine, then acknowledge/remove from outbox
                        var runner = _runnerFactory();
                        await runner.ProcessEventAsync(
                            entry.TaskId,
                            new WorkflowEvent.PermanentFailed(entry.TaskId, permanent, _clock.Now),
                            ct);

                        await _outbox.AcknowledgeAsync(ImmutableArray.Create(entry.Id), ct);
                        return Unit.Value;
                    });
            }
        }
    }

    private async Task<Result<Unit>> DispatchEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        return await entry.Command.Match(
            async submitWorker =>
            {
                var submitResult = await _batchProvider.SubmitAsync(submitWorker.IdemKey, submitWorker.Spec, ct);
                if (submitResult is Result<WorkerHandle>.Err err)
                {
                    return new Result<Unit>.Err(err.Error);
                }

                var handle = submitResult.Unwrap();
                var model = submitWorker.Spec.Match(
                    fresh => fresh.Model,
                    resume => resume.Model);

                // Save worker status in projection
                var upsertResult = await _projectionStore.UpsertWorkerAsync(
                    submitWorker.IdemKey, handle.BatchRef, new WorkerStatus.InFlight(), Usage.Zero, ct, model);
                if (upsertResult is Result<bool>.Err upsertErr)
                {
                    return new Result<Unit>.Err(upsertErr.Error);
                }

                // After successful submit, append the WorkerSubmitted event
                var runner = _runnerFactory();
                var eventResult = await runner.ProcessEventAsync(
                    entry.TaskId,
                    new WorkflowEvent.WorkerSubmitted(entry.TaskId, submitWorker.IdemKey, _clock.Now),
                    ct);

                return eventResult;
            },
            async emitStreamEvent =>
            {
                return await _streamPublisher.PublishAsync(emitStreamEvent.Envelope, ct);
            },
            async scheduleRetry =>
            {
                // Side-effect scheduling: database projection already updated next_attempt_at.
                return Result.Ok(Unit.Value);
            },
            async requeue =>
            {
                // Requeue: DB projection updated state to Queued.
                return Result.Ok(Unit.Value);
            },
            async releaseLease =>
            {
                var releaseRes = await _leaseManager.ReleaseAsync(new LeaseRef(releaseLease.TaskId.Value), ct);
                if (releaseRes is Result<bool>.Err err)
                {
                    return new Result<Unit>.Err(err.Error);
                }
                return Result.Ok(Unit.Value);
            },
            async cancelWorker =>
            {
                var batchRefRes = await _projectionStore.GetWorkerBatchRefAsync(cancelWorker.WorkerId, ct);
                if (batchRefRes is Result<ProviderBatchRef?>.Ok ok && ok.Value is { } batchRef && !string.IsNullOrEmpty(batchRef.Value))
                {
                    var handle = new WorkerHandle(cancelWorker.WorkerId, batchRef);
                    var cancelResult = await _batchProvider.CancelAsync(handle, ct);
                    if (cancelResult is Result<bool>.Err err)
                    {
                        return new Result<Unit>.Err(err.Error);
                    }
                }
                return Result.Ok(Unit.Value);
            });
    }
}
