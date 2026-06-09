using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Submits worker batches to an AI provider (e.g., Anthropic Batch API).
/// Infrastructure adapter — Core only defines the contract.
/// </summary>
public interface IBatchProvider
{
    /// <summary>Submit a worker with the given spec and return a handle for tracking.</summary>
    Task<Result<WorkerHandle>> SubmitAsync(WorkerId idemKey, WorkerSpec spec, CancellationToken ct = default);

    /// <summary>Poll the status of a previously submitted worker.</summary>
    Task<Result<WorkerOutcome>> PollAsync(WorkerHandle handle, CancellationToken ct = default);

    /// <summary>Cancel a running worker.</summary>
    Task<Result<bool>> CancelAsync(WorkerHandle handle, CancellationToken ct = default);
}
