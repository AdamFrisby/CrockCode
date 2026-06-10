using System;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Providers;
using Xunit;

namespace CrockCode.Tests;

public class ProviderTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFunc { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseFunc == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            return Task.FromResult(ResponseFunc(request));
        }
    }

    [Fact]
    public async Task OpenAiBatchProvider_SimulatedOperations_Succeed()
    {
        var provider = new OpenAiBatchProvider();
        var workerId = new WorkerId("wkr_123");
        var spec = new WorkerSpec.Fresh(
            "http://localhost",
            "auth_token",
            "model",
            10,
            "System Prompt"
        );

        // Submit
        var submitRes = await provider.SubmitAsync(workerId, spec);
        Assert.True(submitRes.IsOk);
        var handle = submitRes.Unwrap();
        Assert.Equal(workerId, handle.Id);
        Assert.StartsWith("openai_batch_", handle.BatchRef.Value);

        // Poll
        var pollRes = await provider.PollAsync(handle);
        Assert.True(pollRes.IsOk);
        var outcome = pollRes.Unwrap();
        Assert.Equal(workerId, outcome.WorkerId);
        Assert.IsType<WorkerStatus.Succeeded>(outcome.Status);
        Assert.Equal(1000, outcome.Usage.InputTokens);

        // Cancel
        var cancelRes = await provider.CancelAsync(handle);
        Assert.True(cancelRes.IsOk);
        Assert.True(cancelRes.Unwrap());
    }

    [Fact]
    public async Task CloudflaredTunnelProvider_ProbeAsync_HandlesSuccessAndFailure()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var provider = new CloudflaredTunnelProvider(httpClient);
        var endpoint = new PublicEndpoint("https://cloudflare.try.example");

        // Success path
        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK);
        var successRes = await provider.ProbeAsync(endpoint);
        Assert.True(successRes.IsOk);
        Assert.True(successRes.Unwrap());

        // Error status path
        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var errorStatusRes = await provider.ProbeAsync(endpoint);
        Assert.True(errorStatusRes.IsErr);
        Assert.Equal("PROBE_FAILED", errorStatusRes.UnwrapErr().Match(t => t.Code, p => p.Code));

        // Exception path
        handler.ResponseFunc = req => throw new HttpRequestException("Network error");
        var exceptionRes = await provider.ProbeAsync(endpoint);
        Assert.True(exceptionRes.IsErr);
        Assert.Equal("PROBE_FAILED", exceptionRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task CloudflaredTunnelProvider_StartAsync_FailsCleanly()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var provider = new CloudflaredTunnelProvider(httpClient);

        // Try to start the tunnel. If cloudflared is not installed (likely in sandbox), it will throw TUNNEL_START_FAILED.
        // If it is installed, it might timeout or succeed, but either way it handles clean cleanup.
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Force immediate cancellation to test timeout path if it doesn't fail immediately

        var result = await provider.StartAsync(8080, cts.Token);
        // It should return a Failure result (either start failed or timeout/cancelled)
        Assert.True(result.IsErr);

        await provider.StopAsync();
    }

    [Fact]
    public async Task NgrokTunnelProvider_ProbeAsync_HandlesSuccessAndFailure()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var provider = new NgrokTunnelProvider(httpClient);
        var endpoint = new PublicEndpoint("https://ngrok.example");

        // Success path
        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK);
        var successRes = await provider.ProbeAsync(endpoint);
        Assert.True(successRes.IsOk);
        Assert.True(successRes.Unwrap());

        // Exception path
        handler.ResponseFunc = req => throw new HttpRequestException("Network error");
        var exceptionRes = await provider.ProbeAsync(endpoint);
        Assert.True(exceptionRes.IsErr);
        Assert.Equal("PROBE_FAILED", exceptionRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task NgrokTunnelProvider_StartAsync_FailsCleanly()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var provider = new NgrokTunnelProvider(httpClient);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately to test failure/timeout path

        var result = await provider.StartAsync(8080, cts.Token);
        Assert.True(result.IsErr);

        await provider.StopAsync();
    }
}
