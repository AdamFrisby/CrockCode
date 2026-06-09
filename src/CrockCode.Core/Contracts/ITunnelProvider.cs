using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Core.Contracts;

/// <summary>A publicly accessible endpoint for an MCP tunnel.</summary>
public sealed record PublicEndpoint(string Url);

/// <summary>
/// Manages tunnel lifecycle: start, probe reachability, stop.
/// </summary>
public interface ITunnelProvider
{
    /// <summary>Start a tunnel exposing a local port. Returns the public URL.</summary>
    Task<Result<PublicEndpoint>> StartAsync(int localPort, CancellationToken ct = default);

    /// <summary>Probe reachability of a public endpoint (external round-trip).</summary>
    Task<Result<bool>> ProbeAsync(PublicEndpoint endpoint, CancellationToken ct = default);

    /// <summary>Stop the tunnel.</summary>
    Task StopAsync(CancellationToken ct = default);
}
