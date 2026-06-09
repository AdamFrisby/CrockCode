using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;

namespace CrockCode.Coordinator;

public sealed class ManualTunnelProvider : ITunnelProvider
{
    private readonly string _publicUrl;

    public ManualTunnelProvider(string publicUrl)
    {
        _publicUrl = publicUrl;
    }

    public Task<Result<PublicEndpoint>> StartAsync(int localPort, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Ok(new PublicEndpoint(_publicUrl)));
    }

    public Task<Result<bool>> ProbeAsync(PublicEndpoint endpoint, CancellationToken ct = default)
    {
        return Task.FromResult(Result.Ok(true));
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
