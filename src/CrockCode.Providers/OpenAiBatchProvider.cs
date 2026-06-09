using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Providers;

/// <summary>
/// Stub implementation of IBatchProvider for OpenAI Batch API compatibility.
/// Serves as a compile-time proof of provider-agnosticism.
/// </summary>
public sealed class OpenAiBatchProvider : IBatchProvider
{
    public Task<Result<WorkerHandle>> SubmitAsync(WorkerId idemKey, WorkerSpec spec, CancellationToken ct = default)
    {
        // Simulate immediate submission success with an OpenAI batch ID prefix
        var batchRef = new ProviderBatchRef("openai_batch_" + Guid.NewGuid().ToString("N"));
        var handle = new WorkerHandle(idemKey, batchRef);
        return Task.FromResult(Result.Ok(handle));
    }

    public Task<Result<WorkerOutcome>> PollAsync(WorkerHandle handle, CancellationToken ct = default)
    {
        // Simulate a completed worker outcome with typical OpenAI pricing/usage
        var usage = new Usage(1000, 500, 0.00375m); // Mock price
        var outcome = new WorkerOutcome(handle.Id, usage, new WorkerStatus.Succeeded());
        return Task.FromResult(Result.Ok(outcome));
    }

    public Task<Result<bool>> CancelAsync(WorkerHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Ok(true));
    }
}
