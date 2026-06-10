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

    [Fact]
    public async Task Read_FileNotFound_ReturnsFileNotFound()
    {
        var result = await _codingTools.Read("missing.txt", null, null, default);
        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Read_WithOffsetAndLimit_Succeeds()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        await _codingTools.Write("lines.txt", content, default);

        // Read starting at line 2, limit to 3 lines
        var result = await _codingTools.Read("lines.txt", 2, 3, default);
        Assert.Equal("line2\r\nline3\r\nline4\r\n", result.Replace("\n", "\r\n").Replace("\r\r", "\r"));
    }

    [Fact]
    public async Task Read_DirectoryAsFile_ReturnsFileNotFound()
    {
        // Try reading the working directory itself
        var result = await _codingTools.Read(".", null, null, default);
        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Write_InvalidPath_ReturnsError()
    {
        // Try writing to an invalid path that contains path escape (blocked by ResolveAndValidatePath)
        var result = await _codingTools.Write("../outside.txt", "content", default);
        Assert.Contains("escapes the working directory", result);
    }

    [Fact]
    public async Task Edit_MultipleOccurrences_ReturnsError()
    {
        await _codingTools.Write("doc.txt", "apple orange apple banana", default);

        var result = await _codingTools.Edit("doc.txt", "apple", "peach", default);
        Assert.Contains("Multiple occurrences of old_string found", result);
    }

    [Fact]
    public async Task Edit_FileNotFound_ReturnsError()
    {
        var result = await _codingTools.Edit("missing.txt", "old", "new", default);
        Assert.Contains("File not found", result);
    }

    [Fact]
    public async Task Glob_FindsFiles()
    {
        await _codingTools.Write("src/file1.cs", "content", default);
        await _codingTools.Write("src/file2.cs", "content", default);
        await _codingTools.Write("doc.txt", "content", default);

        var result = await _codingTools.Glob("**/*.cs", null, default);
        Assert.Contains("src/file1.cs", result);
        Assert.Contains("src/file2.cs", result);
        Assert.DoesNotContain("doc.txt", result);
    }

    [Fact]
    public async Task Glob_NoMatches_ReturnsMessage()
    {
        var result = await _codingTools.Glob("*.invalid", null, default);
        Assert.Contains("No files matched the pattern", result);
    }

    [Fact]
    public async Task Grep_FindsMatches()
    {
        await _codingTools.Write("src/file1.cs", "public class A { }", default);
        await _codingTools.Write("src/file2.cs", "private int val;", default);

        var result = await _codingTools.Grep("class", null, default);
        Assert.Contains("src/file1.cs", result);
        Assert.DoesNotContain("src/file2.cs", result);
    }

    [Fact]
    public async Task Bash_ForegroundExecution_Success()
    {
        var result = await _codingTools.Bash("echo 'Hello standard output'", null, null, null, default);
        Assert.Contains("Exit Code: 0", result);
        Assert.Contains("Hello standard output", result);
    }

    [Fact]
    public async Task Bash_BackgroundExecution_AndBashOutput_Success()
    {
        var result = await _codingTools.Bash("echo 'Background Task Output'", null, true, "Test bg", default);
        Assert.Contains("Background task started", result);

        // Parse Task ID
        int idIdx = result.IndexOf("ID: ") + 4;
        int endIdx = result.IndexOf(".", idIdx);
        string taskId = result[idIdx..endIdx];

        // Wait a small moment for background execution to compile output
        await Task.Delay(200);

        var output = await _codingTools.BashOutput(taskId, null, default);
        Assert.Contains($"Task ID: {taskId}", output);
        Assert.Contains("Background Task Output", output);
    }

    [Fact]
    public async Task BashOutput_NotFound_ReturnsError()
    {
        var result = await _codingTools.BashOutput("non-existent-task-id-123", null, default);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Bash_Timeout_ReturnsTimeoutMessage()
    {
        // Execute a command that sleeps for 5 seconds, but set timeout to 1 second
        var result = await _codingTools.Bash("sleep 5", 1, null, null, default);
        Assert.Contains("timed out after 1 seconds", result);
    }
}
