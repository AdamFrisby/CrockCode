using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.McpServer;
using Xunit;

namespace CrockCode.Tests;

public class CodingToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CodingTools _codingTools;

    public CodingToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crock_tools_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var httpContext = new DefaultHttpContext();
        var accessor = new FakeHttpContextAccessor(httpContext);
        var resolver = new FakeMcpContextResolver(new WorkspaceContext(
            new TaskId("tsk_test"),
            new WorkingDir(_tempDir),
            new WorkerId("wkr_test")
        ));
        var bgManager = new BackgroundProcessManager();

        _codingTools = new CodingTools(accessor, resolver, bgManager);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }

        public FakeHttpContextAccessor(HttpContext context)
        {
            HttpContext = context;
        }
    }

    private class FakeMcpContextResolver : IMcpContextResolver
    {
        private readonly WorkspaceContext _context;

        public FakeMcpContextResolver(WorkspaceContext context)
        {
            _context = context;
        }

        public Task<Result<WorkspaceContext>> ResolveContextAsync(HttpContext httpContext, CancellationToken ct = default)
        {
            return Task.FromResult(Result.Ok(_context));
        }
    }

    [Fact]
    public async Task Read_EscapingPath_ReturnsPathEscapeError()
    {
        // Path escaping the working directory
        var result = await _codingTools.Read("../outside.txt", null, null, default);
        
        Assert.Contains("escapes the working directory", result);
    }

    [Fact]
    public async Task Write_And_Read_ValidFile_Succeeds()
    {
        var writeResult = await _codingTools.Write("sub/hello.txt", "Hello World!", default);
        Assert.Contains("Successfully wrote", writeResult);

        var readResult = await _codingTools.Read("sub/hello.txt", null, null, default);
        Assert.Equal("Hello World!", readResult.Trim());
    }

    [Fact]
    public async Task Edit_ValidFile_Succeeds()
    {
        await _codingTools.Write("doc.txt", "The quick brown fox", default);

        var editResult = await _codingTools.Edit("doc.txt", "brown", "red", default);
        Assert.Contains("Successfully edited", editResult);

        var readResult = await _codingTools.Read("doc.txt", null, null, default);
        Assert.Equal("The quick red fox", readResult.Trim());
    }

    [Fact]
    public async Task Edit_MatchNotFound_ReturnsError()
    {
        await _codingTools.Write("doc.txt", "The quick brown fox", default);

        var editResult = await _codingTools.Edit("doc.txt", "blue", "red", default);
        Assert.Contains("The old_string was not found", editResult);
    }
}
