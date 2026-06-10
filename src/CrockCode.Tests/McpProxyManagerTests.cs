using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using CrockCode.Coordinator;
using Xunit;

namespace CrockCode.Tests;

public class McpProxyManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _scriptPath;
    private readonly string _configPath;

    public McpProxyManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp_proxy_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _scriptPath = Path.Combine(_tempDir, "mock_mcp_server.sh");
        _configPath = Path.Combine(_tempDir, "mcp_config.json");

        // Write a simple shell script to mock JSON-RPC stdio exchange
        var scriptContent = @"#!/bin/sh
# 1. Read initialize request
read line
# Write initialize response
echo '{""jsonrpc"":""2.0"",""id"":1,""result"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""serverInfo"":{""name"":""Mock"",""version"":""1.0""}}}'
# 2. Read initialized notification
read line
# 3. Read list tools request or call tool request
while read line; do
  if echo ""$line"" | grep -q ""tools/list""; then
    echo '{""jsonrpc"":""2.0"",""id"":2,""result"":{""tools"":[{""name"":""mock_tool"",""description"":""A mock tool""}]}}'
  elif echo ""$line"" | grep -q ""tools/call""; then
    echo '{""jsonrpc"":""2.0"",""id"":3,""result"":{""content"":[{""type"":""text"",""text"":""Success!""}],""isError"":false}}'
  fi
done
";
        File.WriteAllText(_scriptPath, scriptContent);

        // Make executable (Linux specific)
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x {_scriptPath}",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit();
        }
        catch
        {
            // Ignore if chmod fails
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore
        }
    }

    [Fact]
    public async Task McpProxyManager_InitializeWithMissingConfig_ReturnsEarly()
    {
        await using var manager = new McpProxyManager("non_existent_file.json", NullLogger<McpProxyManager>.Instance);
        await manager.InitializeAsync(default);

        var tools = await manager.GetExternalToolsAsync(default);
        Assert.Empty(tools);
        Assert.False(manager.IsExternalTool("some_tool"));
    }

    [Fact]
    public async Task McpProxyManager_InvalidCommand_HandlesExceptionGracefully()
    {
        var configContent = @"{
  ""mcpServers"": {
    ""invalid-server"": {
      ""command"": ""non-existent-binary-xyz"",
      ""args"": []
    }
  }
}";
        File.WriteAllText(_configPath, configContent);

        await using var manager = new McpProxyManager(_configPath, NullLogger<McpProxyManager>.Instance);
        await manager.InitializeAsync(default);

        var tools = await manager.GetExternalToolsAsync(default);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpProxyManager_MockServerExchange_Success()
    {
        var configContent = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                {
                    "mock-server", new
                    {
                        command = _scriptPath,
                        args = new List<string>(),
                        env = new Dictionary<string, string> { { "MOCK_ENV", "1" } }
                    }
                }
            }
        });
        File.WriteAllText(_configPath, configContent);

        var manager = new McpProxyManager(_configPath, NullLogger<McpProxyManager>.Instance);
        try
        {
            // 1. Get tools
            var tools = await manager.GetExternalToolsAsync(default);
            Assert.Single(tools);
            Assert.Equal("mock_tool", tools[0].Name);

            // 2. Check tool exists
            Assert.True(manager.IsExternalTool("mock_tool"));
            Assert.False(manager.IsExternalTool("unknown_tool"));

            // 3. Call tool
            var callResult = await manager.CallExternalToolAsync("mock_tool", null, default);
            Assert.False(callResult.IsError);
            Assert.Single(callResult.Content);
            Assert.Equal("Success!", ((ModelContextProtocol.Protocol.TextContentBlock)callResult.Content[0]).Text);

            // 4. Call non-existent tool
            var missingResult = await manager.CallExternalToolAsync("missing_tool", null, default);
            Assert.True(missingResult.IsError);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }
}
