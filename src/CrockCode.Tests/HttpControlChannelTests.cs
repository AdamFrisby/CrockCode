using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Cli;
using Xunit;

namespace CrockCode.Tests;

public class HttpControlChannelTests
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

    private (HttpControlChannel Channel, MockHttpMessageHandler Handler) CreateChannel()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var channel = new HttpControlChannel(httpClient);
        return (channel, handler);
    }

    [Fact]
    public async Task EnqueueTaskAsync_Success_ReturnsTaskId()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { taskId = "tsk_123" })
        };

        var res = await channel.EnqueueTaskAsync(
            new WorkingDir("/tmp"), "My Prompt", new Priority(3), 3,
            ImmutableArray.Create("tool_1"), ImmutableArray.Create("tool_2")
        );

        Assert.True(res.IsOk);
        Assert.Equal("tsk_123", res.Unwrap().Value);
    }

    [Fact]
    public async Task EnqueueTaskAsync_HttpError_ReturnsCliError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Invalid request")
        };

        var res = await channel.EnqueueTaskAsync(
            new WorkingDir("/tmp"), "My Prompt", new Priority(3), 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        Assert.True(res.IsErr);
        Assert.Equal("CLI_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task EnqueueTaskAsync_InvalidResponse_ReturnsCliError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { invalidField = "" })
        };

        var res = await channel.EnqueueTaskAsync(
            new WorkingDir("/tmp"), "My Prompt", new Priority(3), 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        Assert.True(res.IsErr);
        Assert.Equal("CLI_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task EnqueueTaskAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Timeout");

        var res = await channel.EnqueueTaskAsync(
            new WorkingDir("/tmp"), "My Prompt", new Priority(3), 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task CancelTaskAsync_Success_ReturnsTrue()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK);

        var res = await channel.CancelTaskAsync(new TaskId("tsk_123"));
        Assert.True(res.IsOk);
        Assert.True(res.Unwrap());
    }

    [Fact]
    public async Task CancelTaskAsync_HttpError_ReturnsCliError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Task not found")
        };

        var res = await channel.CancelTaskAsync(new TaskId("tsk_123"));
        Assert.True(res.IsErr);
        Assert.Equal("CLI_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task CancelTaskAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var res = await channel.CancelTaskAsync(new TaskId("tsk_123"));
        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task GetTaskStateAsync_Success_ReturnsState()
    {
        var (channel, handler) = CreateChannel();

        var taskId = new TaskId("tsk_123");
        var state = new WorkflowState.Queued(
            taskId, new WorkingDir("/tmp"), "Prompt", new Priority(3), 1, 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(state, typeof(WorkflowState))
        };

        var res = await channel.GetTaskStateAsync(taskId);
        Assert.True(res.IsOk);
        Assert.IsType<WorkflowState.Queued>(res.Unwrap());
    }

    [Fact]
    public async Task GetTaskStateAsync_HttpError_ReturnsCliError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var res = await channel.GetTaskStateAsync(new TaskId("tsk_123"));
        Assert.True(res.IsErr);
        Assert.Equal("CLI_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task GetTaskStateAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var res = await channel.GetTaskStateAsync(new TaskId("tsk_123"));
        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ListTasksAsync_Success_ReturnsTasks()
    {
        var (channel, handler) = CreateChannel();

        var taskId = new TaskId("tsk_123");
        var state = new WorkflowState.Queued(
            taskId, new WorkingDir("/tmp"), "Prompt", new Priority(3), 1, 3,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty
        );

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(ImmutableArray.Create<WorkflowState>(state))
        };

        var res = await channel.ListTasksAsync();
        Assert.True(res.IsOk);
        Assert.Single(res.Unwrap());
        Assert.Equal(taskId, res.Unwrap()[0].TaskId);
    }

    [Fact]
    public async Task ListTasksAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var res = await channel.ListTasksAsync();
        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task GetTunnelUrlAsync_Success_ReturnsUrl()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { url = "https://tunnel.try.example" })
        };

        var res = await channel.GetTunnelUrlAsync();
        Assert.True(res.IsOk);
        Assert.Equal("https://tunnel.try.example", res.Unwrap());
    }

    [Fact]
    public async Task GetTunnelUrlAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var res = await channel.GetTunnelUrlAsync();
        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task ProbeTunnelAsync_Success_ReturnsSuccessBool()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { success = true })
        };

        var res = await channel.ProbeTunnelAsync();
        Assert.True(res.IsOk);
        Assert.True(res.Unwrap());
    }

    [Fact]
    public async Task ProbeTunnelAsync_Exception_ReturnsConnectionError()
    {
        var (channel, handler) = CreateChannel();

        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var res = await channel.ProbeTunnelAsync();
        Assert.True(res.IsErr);
        Assert.Equal("CONNECTION_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task FollowStreamAsync_ReadsEvents_Successfully()
    {
        var (channel, handler) = CreateChannel();

        var envelope = new StreamEnvelope("TaskEvent", new TaskId("tsk_123"), 1L, JsonSerializer.SerializeToElement(new { val = 100 }));
        string json = JsonSerializer.Serialize(envelope);

        string sseData = $"data: {json}\n\n";

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseData)
        };

        var envelopes = new List<StreamEnvelope>();
        await foreach (var env in channel.FollowStreamAsync())
        {
            envelopes.Add(env);
        }

        Assert.Single(envelopes);
        Assert.Equal("TaskEvent", envelopes[0].Type);
        Assert.Equal("tsk_123", envelopes[0].TaskId.Value);
    }
}
