using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Distributed lease manager for workspace-level mutual exclusion.
/// Ensures only one task at a time operates on a given working directory.
/// </summary>
public interface ILeaseManager
{
    /// <summary>Attempt to acquire a lease for a working directory.</summary>
    Task<Result<LeaseDisposition>> AcquireAsync(
        WorkingDir workingDir, TaskId taskId, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Renew an existing lease.</summary>
    Task<Result<bool>> RenewAsync(LeaseRef leaseRef, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Release a lease.</summary>
    Task<Result<bool>> ReleaseAsync(LeaseRef leaseRef, CancellationToken ct = default);

    /// <summary>Check if a lease is still held.</summary>
    Task<Result<bool>> IsHeldAsync(LeaseRef leaseRef, CancellationToken ct = default);
}
