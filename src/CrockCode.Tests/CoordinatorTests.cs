using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Coordinator;
using Xunit;

namespace CrockCode.Tests;

public class CoordinatorTests
{
    [Fact]
    public void HmacTokenSigner_SignAndVerify_Success()
    {
        var signer = new HmacTokenSigner();
        var context = new WorkspaceContext(new TaskId("tsk_1"), new WorkingDir("/tmp"), new WorkerId("wkr_1"));
        
        var signRes = signer.Sign(context, TimeSpan.FromMinutes(5));
        Assert.True(signRes.IsOk);

        var token = signRes.Unwrap();
        Assert.False(string.IsNullOrEmpty(token.Value));

        var verifyRes = signer.Verify(token);
        Assert.True(verifyRes.IsOk);

        var verified = verifyRes.Unwrap();
        Assert.Equal(context.TaskId, verified.TaskId);
        Assert.Equal(context.WorkingDir, verified.WorkingDir);
        Assert.Equal(context.WorkerId, verified.WorkerId);
    }

    [Fact]
    public void HmacTokenSigner_Verify_InvalidTokenFormat_ReturnsError()
    {
        var signer = new HmacTokenSigner();
        
        var verifyRes = signer.Verify(new WorkspaceToken("invalidtoken"));
        Assert.True(verifyRes.IsErr);
        Assert.Equal("INVALID_TOKEN", verifyRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void HmacTokenSigner_Verify_InvalidSignature_ReturnsError()
    {
        var signer = new HmacTokenSigner();
        var context = new WorkspaceContext(new TaskId("tsk_1"), new WorkingDir("/tmp"), new WorkerId("wkr_1"));
        var token = signer.Sign(context, TimeSpan.FromMinutes(5)).Unwrap();

        // Corrupt signature
        string corruptedToken = token.Value + "extra";
        var verifyRes = signer.Verify(new WorkspaceToken(corruptedToken));
        Assert.True(verifyRes.IsErr);
        Assert.Equal("INVALID_SIGNATURE", verifyRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void HmacTokenSigner_Verify_ExpiredToken_ReturnsError()
    {
        var signer = new HmacTokenSigner();
        var context = new WorkspaceContext(new TaskId("tsk_1"), new WorkingDir("/tmp"), new WorkerId("wkr_1"));
        
        // Sign with negative validity
        var token = signer.Sign(context, TimeSpan.FromMinutes(-5)).Unwrap();

        var verifyRes = signer.Verify(token);
        Assert.True(verifyRes.IsErr);
        Assert.Equal("TOKEN_EXPIRED", verifyRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void HmacTokenSigner_Verify_Exception_ReturnsVerifyFailed()
    {
        var signer = new HmacTokenSigner();
        var verifyRes = signer.Verify(new WorkspaceToken(null!));
        Assert.True(verifyRes.IsErr);
        Assert.Equal("VERIFY_FAILED", verifyRes.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public void SystemClock_ReturnsNow()
    {
        var clock = new SystemClock();
        var now = clock.Now;
        var utcNow = DateTimeOffset.UtcNow;
        Assert.True((now.Value - utcNow).Duration() < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SystemRandom_ReturnsNumbersInRanges()
    {
        var rand = new SystemRandom();
        var val = rand.Next(10, 20);
        Assert.True(val >= 10 && val < 20);

        var valDouble = rand.NextDouble();
        Assert.True(valDouble >= 0.0 && valDouble < 1.0);
    }

    [Fact]
    public void GuidIdFactory_GeneratesValidIds()
    {
        var factory = new GuidIdFactory();
        
        var taskId = factory.NewTaskId();
        Assert.StartsWith("tsk_", taskId.Value);

        var workerId = factory.NewWorkerId();
        Assert.StartsWith("wkr_", workerId.Value);

        var commandId = factory.NewCommandId();
        Assert.StartsWith("cmd_", commandId.Value);

        var leaseRef = factory.NewLeaseRef();
        Assert.StartsWith("lease_", leaseRef.Value);
    }

    [Fact]
    public async Task StreamEventPublisher_SubscribePublishAndUnsubscribe_Success()
    {
        var publisher = new StreamEventPublisher();
        var context = new DefaultHttpContext();
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        // Subscribe
        publisher.Subscribe("sub_1", context.Response);

        // Publish
        var envelope = new StreamEnvelope("TestEvent", new TaskId("tsk_1"), 1L, JsonSerializer.SerializeToElement(new { val = 42 }));
        var publishRes = await publisher.PublishAsync(envelope);
        Assert.True(publishRes.IsOk);

        // Verify content written to stream
        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        string written = await reader.ReadToEndAsync();
        Assert.StartsWith("data: {", written);
        Assert.Contains("TestEvent", written);
        Assert.Contains("tsk_1", written);

        // Unsubscribe
        publisher.Unsubscribe("sub_1");
        
        // Reset memory stream and publish again (should write nothing)
        memoryStream.SetLength(0);
        await publisher.PublishAsync(envelope);
        Assert.Equal(0, memoryStream.Length);
    }

    private class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new IOException("Simulated network closed exception");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamEventPublisher_PublishFails_UnsubscribesAutomatically()
    {
        var publisher = new StreamEventPublisher();
        var context = new DefaultHttpContext();
        context.Response.Body = new FailingStream();

        publisher.Subscribe("sub_fail", context.Response);

        var envelope = new StreamEnvelope("TestEvent", new TaskId("tsk_1"), 1L, JsonSerializer.SerializeToElement(new { val = 42 }));
        var publishRes = await publisher.PublishAsync(envelope);
        
        Assert.True(publishRes.IsOk);
    }

    [Fact]
    public async Task ManualTunnelProvider_WorksAsExpected()
    {
        var provider = new ManualTunnelProvider("https://tunnel.example.com");

        var startRes = await provider.StartAsync(8080);
        Assert.True(startRes.IsOk);
        Assert.Equal("https://tunnel.example.com", startRes.Unwrap().Url);

        var probeRes = await provider.ProbeAsync(new PublicEndpoint("https://tunnel.example.com"));
        Assert.True(probeRes.IsOk);
        Assert.True(probeRes.Unwrap());

        await provider.StopAsync();
    }
}
