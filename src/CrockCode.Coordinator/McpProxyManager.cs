using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace CrockCode.Coordinator;

public sealed class McpProxyManager : IAsyncDisposable
{
    private readonly string _configPath;
    private readonly ILogger<McpProxyManager> _logger;
    private readonly ConcurrentDictionary<string, ExternalServerProcess> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _toolToSeverMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public McpProxyManager(string configPath, ILogger<McpProxyManager> logger)
    {
        _configPath = configPath;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_loaded || string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(_configPath, ct);
            var config = JsonSerializer.Deserialize<McpConfigRoot>(content);
            if (config?.McpServers == null)
            {
                return;
            }

            foreach (var (name, serverConfig) in config.McpServers)
            {
                if (string.IsNullOrEmpty(serverConfig.Command)) continue;

                var proc = new ExternalServerProcess(name, serverConfig, _logger);
                _servers[name] = proc;
                _logger.LogInformation("Registered external MCP server: {Name} ({Command})", name, serverConfig.Command);
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load MCP configuration from {Path}", _configPath);
        }
    }

    public async Task<List<Tool>> GetExternalToolsAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);

        var allTools = new List<Tool>();
        _toolToSeverMap.Clear();

        foreach (var (name, proc) in _servers)
        {
            try
            {
                var tools = await proc.ListToolsAsync(ct);
                foreach (var tool in tools)
                {
                    allTools.Add(tool);
                    _toolToSeverMap[tool.Name] = name;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list tools from external MCP server {Name}", name);
            }
        }

        return allTools;
    }

    public bool IsExternalTool(string toolName)
    {
        return _toolToSeverMap.ContainsKey(toolName);
    }

    public async Task<CallToolResult> CallExternalToolAsync(string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (!_toolToSeverMap.TryGetValue(toolName, out var serverName) || !_servers.TryGetValue(serverName, out var proc))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = $"Error: External tool {toolName} not found." } }
            };
        }

        try
        {
            return await proc.CallToolAsync(toolName, arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed calling external tool {ToolName} on server {ServerName}", toolName, serverName);
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = $"Proxy Error: {ex.Message}" } }
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var proc in _servers.Values)
        {
            await proc.DisposeAsync();
        }
        _servers.Clear();
    }

    private class McpConfigRoot
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerConfig>? McpServers { get; set; }
    }

    public class McpServerConfig
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = "";

        [JsonPropertyName("args")]
        public List<string> Args { get; set; } = new();

        [JsonPropertyName("env")]
        public Dictionary<string, string>? Env { get; set; }
    }

    private class ExternalServerProcess : IAsyncDisposable
    {
        private readonly string _name;
        private readonly McpServerConfig _config;
        private readonly ILogger _logger;
        private Process? _process;
        private StreamWriter? _stdin;
        private StreamReader? _stdout;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private int _requestIdSequence;

        public ExternalServerProcess(string name, McpServerConfig config, ILogger logger)
        {
            _name = name;
            _config = config;
            _logger = logger;
        }

        private async Task EnsureRunningAsync(CancellationToken ct)
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            await _lock.WaitAsync(ct);
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    return;
                }

                await CleanupAsync();

                _logger.LogInformation("Starting MCP server process '{Name}': {Command} {Args}", _name, _config.Command, string.Join(" ", _config.Args));
                var psi = new ProcessStartInfo(_config.Command)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var arg in _config.Args)
                {
                    psi.ArgumentList.Add(arg);
                }

                if (_config.Env != null)
                {
                    foreach (var (k, v) in _config.Env)
                    {
                        psi.Environment[k] = v;
                    }
                }

                _process = Process.Start(psi);
                if (_process == null)
                {
                    throw new Exception($"Failed to start process for {_name}");
                }

                _stdin = _process.StandardInput;
                _stdout = _process.StandardOutput;

                // Send JSON-RPC initialize request
                var initId = Interlocked.Increment(ref _requestIdSequence);
                var initRequest = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    id = initId,
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { },
                        clientInfo = new { name = "CrockCode-Coordinator", version = "1.0.0" }
                    }
                };

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(initRequest));
                await _stdin.FlushAsync();

                var initRespLine = await _stdout.ReadLineAsync(ct);
                // Initialize notification
                var initNotification = new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized"
                };
                await _stdin.WriteLineAsync(JsonSerializer.Serialize(initNotification));
                await _stdin.FlushAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<List<Tool>> ListToolsAsync(CancellationToken ct)
        {
            await EnsureRunningAsync(ct);

            await _lock.WaitAsync(ct);
            try
            {
                var id = Interlocked.Increment(ref _requestIdSequence);
                var req = new
                {
                    jsonrpc = "2.0",
                    method = "tools/list",
                    id = id,
                    @params = new { }
                };

                await _stdin!.WriteLineAsync(JsonSerializer.Serialize(req));
                await _stdin.FlushAsync();

                var line = await _stdout!.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line))
                {
                    throw new Exception("Received empty response from server.");
                }

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("result", out var resultElem) &&
                    resultElem.TryGetProperty("tools", out var toolsElem))
                {
                    var tools = JsonSerializer.Deserialize<List<Tool>>(toolsElem.GetRawText());
                    return tools ?? new List<Tool>();
                }

                return new List<Tool>();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<CallToolResult> CallToolAsync(string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
        {
            await EnsureRunningAsync(ct);

            await _lock.WaitAsync(ct);
            try
            {
                var id = Interlocked.Increment(ref _requestIdSequence);
                var req = new
                {
                    jsonrpc = "2.0",
                    method = "tools/call",
                    id = id,
                    @params = new
                    {
                        name = toolName,
                        arguments = arguments ?? (object)new Dictionary<string, JsonElement>()
                    }
                };

                await _stdin!.WriteLineAsync(JsonSerializer.Serialize(req));
                await _stdin.FlushAsync();

                var line = await _stdout!.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line))
                {
                    throw new Exception("Received empty response from server.");
                }

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("result", out var resultElem))
                {
                    var result = JsonSerializer.Deserialize<CallToolResult>(resultElem.GetRawText());
                    return result ?? new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = "Failed to deserialize result" } } };
                }

                return new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = "No result property in response" } } };
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task CleanupAsync()
        {
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    await _process.WaitForExitAsync();
                }
                catch
                {
                    // Ignore
                }
                _process.Dispose();
                _process = null;
            }
            _stdin = null;
            _stdout = null;
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
            _lock.Dispose();
        }
    }
}
