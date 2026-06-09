using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>
/// Publishes workflow stream events to active subscribers (e.g. CLI via SSE).
/// </summary>
public interface IStreamEventPublisher
{
    /// <summary>Publishes an event envelope to the stream.</summary>
    Task<Result<Unit>> PublishAsync(StreamEnvelope envelope, CancellationToken ct = default);
}
