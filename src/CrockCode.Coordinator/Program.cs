using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Engine;
using CrockCode.Storage;
using CrockCode.Providers;
using CrockCode.McpServer;
using CrockCode.Coordinator;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Ensure ~/.crockcode exists
var crockHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
Directory.CreateDirectory(crockHome);
var dbPath = Path.Combine(crockHome, "queue.db");

// Load configuration
var config = CrockConfig.Load();
builder.Services.AddSingleton(config);

// Register Core Infrastructure
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IRandom, SystemRandom>();
builder.Services.AddSingleton<IIdFactory, GuidIdFactory>();
builder.Services.AddSingleton<ITokenSigner, HmacTokenSigner>();

// Register Sqlite Storage
var sqliteStore = new SqliteStore(dbPath, new SystemClock());
builder.Services.AddSingleton<IEventStore>(sqliteStore);
builder.Services.AddSingleton<IProjectionStore>(sqliteStore);
builder.Services.AddSingleton<IOutbox>(sqliteStore);
builder.Services.AddSingleton<IJobStore>(sqliteStore);
builder.Services.AddSingleton<ILeaseManager>(sqliteStore);

// Register Engine Components
builder.Services.AddTransient<WorkflowRunner>();
builder.Services.AddTransient<OutboxDispatcher>();
builder.Services.AddSingleton<Func<WorkflowRunner>>(sp => () => sp.GetRequiredService<WorkflowRunner>());
builder.Services.AddSingleton<StreamEventPublisher>();
builder.Services.AddSingleton<IStreamEventPublisher>(sp => sp.GetRequiredService<StreamEventPublisher>());
builder.Services.AddSingleton<IControlChannel, ControlChannel>();

// Register Providers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITunnelProvider>(sp =>
{
    var cfg = sp.GetRequiredService<CrockConfig>();
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    return cfg.TunnelProvider.ToLowerInvariant() switch
    {
        "ngrok" => new NgrokTunnelProvider(httpClient),
        "manual" => new ManualTunnelProvider(cfg.McpPublicUrl),
        _ => new CloudflaredTunnelProvider(httpClient)
    };
});

builder.Services.AddTransient<IBatchProvider>(sp => 
{
    var cfg = sp.GetRequiredService<CrockConfig>();
    if (string.Equals(cfg.Provider, "openai", StringComparison.OrdinalIgnoreCase))
    {
        return new OpenAiBatchProvider();
    }
    return new AnthropicBatchProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), cfg.AnthropicApiKey);
});

builder.Services.AddSingleton<McpProxyManager>(sp =>
{
    var cfg = sp.GetRequiredService<CrockConfig>();
    var logger = sp.GetRequiredService<ILogger<McpProxyManager>>();
    return new McpProxyManager(cfg.McpConfig, logger);
});

// Register McpServer Services
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IMcpContextResolver, McpContextResolver>();
builder.Services.AddSingleton<BackgroundProcessManager>();

// Register MCP server tools using assembly scanning
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next => async (context, ct) =>
        {
            var result = await next(context, ct);
            var httpContext = context.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (httpContext == null) return result;

            var resolver = context.Services!.GetRequiredService<IMcpContextResolver>();
            var ctxRes = await resolver.ResolveContextAsync(httpContext, ct);
            if (ctxRes is Result<WorkspaceContext>.Err) return result;
            var ctx = ctxRes.Unwrap();

            var store = context.Services!.GetRequiredService<IProjectionStore>();
            var stateRes = await store.LoadAsync(ctx.TaskId, ct);
            if (stateRes is not Result<WorkflowState?>.Ok stateOk || stateOk.Value == null) return result;
            var state = stateOk.Value;

            var modelRes = await store.GetWorkerModelAsync(ctx.WorkerId, ct);
            string? model = modelRes is Result<string?>.Ok mOk ? mOk.Value : null;

            var modelTools = ToolSchemaRegistry.GetToolsForModel(model);
            var allowed = state.AllowedTools;
            var disallowed = state.DisallowedTools;

            var filteredTools = new List<ModelContextProtocol.Protocol.Tool>();
            foreach (var tool in result.Tools)
            {
                var matchedModelTool = modelTools.FirstOrDefault(mt => string.Equals(mt.Name, tool.Name, StringComparison.OrdinalIgnoreCase));
                if (matchedModelTool == null) continue;

                if (allowed.Length > 0 && !allowed.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (disallowed.Contains(tool.Name, StringComparer.OrdinalIgnoreCase)) continue;

                tool.InputSchema = matchedModelTool.InputSchema;
                tool.Description = matchedModelTool.Description;
                filteredTools.Add(tool);
            }

            // Load and append external tools
            var proxyManager = context.Services!.GetRequiredService<McpProxyManager>();
            var externalTools = await proxyManager.GetExternalToolsAsync(ct);
            foreach (var extTool in externalTools)
            {
                if (allowed.Length > 0 && !allowed.Contains(extTool.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (disallowed.Contains(extTool.Name, StringComparer.OrdinalIgnoreCase)) continue;

                filteredTools.Add(extTool);
            }

            result.Tools = filteredTools;
            return result;
        });

        filters.AddCallToolFilter(next => async (context, ct) =>
        {
            var toolName = context.Params.Name;
            var httpContext = context.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (httpContext == null)
            {
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = "Error: No active HTTP request" }
                    }
                };
            }

            var resolver = context.Services!.GetRequiredService<IMcpContextResolver>();
            var ctxRes = await resolver.ResolveContextAsync(httpContext, ct);
            if (ctxRes is Result<WorkspaceContext>.Err err)
            {
                var detail = err.Error.Match(t => t.Detail, p => p.Detail);
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"UNAUTHORIZED: {detail}" }
                    }
                };
            }

            var ctx = ctxRes.Unwrap();

            // Verify worker is assigned to task
            var projectionStore = context.Services!.GetRequiredService<IProjectionStore>();
            var activeTaskRes = await projectionStore.LoadByWorkerAsync(ctx.WorkerId, ct);
            if (activeTaskRes is not Result<WorkflowState?>.Ok activeOk || activeOk.Value == null)
            {
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = "Error: Worker is not currently bound to an active running task." }
                    }
                };
            }

            var state = activeOk.Value;

            if (state is WorkflowState.Dispatched dispatchedState)
            {
                var leaseManager = context.Services!.GetRequiredService<ILeaseManager>();
                var leaseRes = await leaseManager.AcquireAsync(state.WorkingDir, state.TaskId, TimeSpan.FromHours(24), ct);
                if (leaseRes is not Result<LeaseDisposition>.Ok leaseOk)
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                        {
                            new ModelContextProtocol.Protocol.TextContentBlock { Text = "Error: Failed to acquire directory lease." }
                        }
                    };
                }

                var leaseRef = leaseOk.Value.Match(
                    acquired => (LeaseRef?)acquired.Lease,
                    joined => (LeaseRef?)joined.Lease,
                    blocked => (LeaseRef?)null
                );

                if (leaseRef == null)
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                        {
                            new ModelContextProtocol.Protocol.TextContentBlock { Text = "Error: Directory lease is blocked by another task tree." }
                        }
                    };
                }

                var runner = context.Services!.GetRequiredService<WorkflowRunner>();
                var clock = context.Services!.GetRequiredService<IClock>();
                var claimResult = await runner.ProcessEventAsync(
                    state.TaskId,
                    new WorkflowEvent.TaskClaimed(state.TaskId, ctx.WorkerId, leaseRef.Value, clock.Now),
                    ct);

                if (claimResult is Result<Unit>.Err claimErr)
                {
                    return new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                        {
                            new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Error claiming task: {claimErr.Error.Match(t => t.Detail, p => p.Detail)}" }
                        }
                    };
                }

                var reloadRes = await projectionStore.LoadAsync(state.TaskId, ct);
                if (reloadRes is Result<WorkflowState?>.Ok reloadOk && reloadOk.Value != null)
                {
                    state = reloadOk.Value;
                }
            }

            var allowed = state.AllowedTools;
            var disallowed = state.DisallowedTools;

            if (allowed.Length > 0 && !allowed.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Error: Tool {toolName} is not allowed for this task." }
                    }
                };
            }

            if (disallowed.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Error: Tool {toolName} is disallowed for this task." }
                    }
                };
            }

            var proxyManager = context.Services!.GetRequiredService<McpProxyManager>();
            if (proxyManager.IsExternalTool(toolName))
            {
                // Verify path containment for external tools too
                if (context.Params.Arguments != null)
                {
                    string[] fileKeys = { "file_path", "path" };
                    foreach (var key in fileKeys)
                    {
                        if (context.Params.Arguments.TryGetValue(key, out var pathElement) && pathElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string relativePath = pathElement.GetString() ?? "";
                            try
                            {
                                string canonicalWorkingDir = Path.GetFullPath(ctx.WorkingDir.Value);
                                string combinedPath = Path.Combine(canonicalWorkingDir, relativePath);
                                string canonicalPath = Path.GetFullPath(combinedPath);

                                if (!canonicalPath.StartsWith(canonicalWorkingDir, StringComparison.Ordinal))
                                {
                                    return new ModelContextProtocol.Protocol.CallToolResult
                                    {
                                        IsError = true,
                                        Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                                        {
                                            new ModelContextProtocol.Protocol.TextContentBlock { Text = $"PATH_ESCAPE: Path '{relativePath}' escapes the working directory." }
                                        }
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                return new ModelContextProtocol.Protocol.CallToolResult
                                {
                                    IsError = true,
                                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                                    {
                                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"INVALID_PATH: {ex.Message}" }
                                    }
                                };
                            }
                        }
                    }
                }

                try
                {
                    var result = await proxyManager.CallExternalToolAsync(toolName, context.Params.Arguments, ct);
                    string argumentsJson = context.Params.Arguments != null ? JsonSerializer.Serialize(context.Params.Arguments) : "{}";
                    string resultJson = JsonSerializer.Serialize(result);
                    await projectionStore.RecordToolCallAsync(ctx.TaskId, toolName, argumentsJson, resultJson, ct);
                    return result;
                }
                catch (Exception ex)
                {
                    var result = new ModelContextProtocol.Protocol.CallToolResult
                    {
                        IsError = true,
                        Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                        {
                            new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Execution Error: {ex.Message}" }
                        }
                    };
                    string argumentsJson = context.Params.Arguments != null ? JsonSerializer.Serialize(context.Params.Arguments) : "{}";
                    string resultJson = JsonSerializer.Serialize(result);
                    await projectionStore.RecordToolCallAsync(ctx.TaskId, toolName, argumentsJson, resultJson, ct);
                    return result;
                }
            }

            if (context.Params.Arguments != null)
            {
                string[] fileKeys = { "file_path", "path" };
                foreach (var key in fileKeys)
                {
                    if (context.Params.Arguments.TryGetValue(key, out var pathElement) && pathElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string relativePath = pathElement.GetString() ?? "";
                        try
                        {
                            string canonicalWorkingDir = Path.GetFullPath(ctx.WorkingDir.Value);
                            string combinedPath = Path.Combine(canonicalWorkingDir, relativePath);
                            string canonicalPath = Path.GetFullPath(combinedPath);

                            if (!canonicalPath.StartsWith(canonicalWorkingDir, StringComparison.Ordinal))
                            {
                                return new ModelContextProtocol.Protocol.CallToolResult
                                {
                                    IsError = true,
                                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                                    {
                                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"PATH_ESCAPE: Path '{relativePath}' escapes the working directory." }
                                    }
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            return new ModelContextProtocol.Protocol.CallToolResult
                            {
                                IsError = true,
                                Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                                {
                                    new ModelContextProtocol.Protocol.TextContentBlock { Text = $"INVALID_PATH: {ex.Message}" }
                                }
                            };
                        }
                    }
                }

                if (string.Equals(toolName, "Write", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.Params.Arguments.TryGetValue("file_contents", out var fileContentsVal))
                    {
                        context.Params.Arguments["content"] = fileContentsVal;
                        context.Params.Arguments.Remove("file_contents");
                    }
                }
            }

            try
            {
                var result = await next(context, ct);
                string argumentsJson = context.Params.Arguments != null ? JsonSerializer.Serialize(context.Params.Arguments) : "{}";
                string resultJson = JsonSerializer.Serialize(result);
                await projectionStore.RecordToolCallAsync(ctx.TaskId, toolName, argumentsJson, resultJson, ct);
                return result;
            }
            catch (Exception ex)
            {
                var result = new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = new List<ModelContextProtocol.Protocol.ContentBlock>
                    {
                        new ModelContextProtocol.Protocol.TextContentBlock { Text = $"Execution Error: {ex.Message}" }
                    }
                };
                string argumentsJson = context.Params.Arguments != null ? JsonSerializer.Serialize(context.Params.Arguments) : "{}";
                string resultJson = JsonSerializer.Serialize(result);
                await projectionStore.RecordToolCallAsync(ctx.TaskId, toolName, argumentsJson, resultJson, ct);
                return result;
            }
        });
    });

// Add background pool manager service
builder.Services.AddHostedService<PoolManagerService>();

var app = builder.Build();

app.UseRouting();

// Health check for reachability probes
app.MapGet("/health", () => Results.Ok("OK"));

// Map the HTTP-based MCP server endpoint
app.MapMcp(pattern: "api/mcp");

// Loopback control APIs for the thin CLI client
app.MapPost("/api/tasks", async (EnqueueRequest req, IControlChannel channel) =>
{
    var allowedArray = req.AllowedTools == null ? ImmutableArray<string>.Empty : req.AllowedTools.ToImmutableArray();
    var disallowedArray = req.DisallowedTools == null ? ImmutableArray<string>.Empty : req.DisallowedTools.ToImmutableArray();
    TaskId? parentId = string.IsNullOrEmpty(req.ParentId) ? null : new TaskId(req.ParentId);
    var res = await channel.EnqueueTaskAsync(
        new WorkingDir(req.WorkingDir), req.Prompt, new Priority(req.Priority), req.MaxAttempts,
        allowedArray, disallowedArray, parentId);

    return res.Match(
        id => Results.Ok(new { taskId = id.Value }),
        err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
    );
});

app.MapDelete("/api/tasks/{id}", async (string id, IControlChannel channel) =>
{
    var res = await channel.CancelTaskAsync(new TaskId(id));
    return res.Match(
        success => Results.Ok(new { success }),
        err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
    );
});

app.MapGet("/api/tasks/{id}", async (string id, IControlChannel channel) =>
{
    var res = await channel.GetTaskStateAsync(new TaskId(id));
    return res.Match(
        state => Results.Ok(state),
        err => Results.NotFound(err.Match(t => t.Detail, p => p.Detail))
    );
});

app.MapGet("/api/tasks", async (IProjectionStore store) =>
{
    var res = await store.ListTasksAsync();
    return res.Match(
        tasks => Results.Ok(tasks),
        err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
    );
});

app.MapGet("/api/stream", async (HttpContext httpContext, StreamEventPublisher publisher) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    string clientId = Guid.NewGuid().ToString("N");
    publisher.Subscribe(clientId, httpContext.Response);

    var tcs = new TaskCompletionSource();
    httpContext.RequestAborted.Register(() =>
    {
        publisher.Unsubscribe(clientId);
        tcs.SetResult();
    });

});

app.MapGet("/api/tunnel", async (ITunnelProvider tunnelProvider, CrockConfig config) =>
{
    var res = await tunnelProvider.StartAsync(config.LocalPort);
    return res.Match(
        endpoint => Results.Ok(new { url = endpoint.Url }),
        err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
    );
});

app.MapPost("/api/tunnel/probe", async (ITunnelProvider tunnelProvider, CrockConfig config) =>
{
    var res = await tunnelProvider.StartAsync(config.LocalPort);
    if (res is Result<PublicEndpoint>.Ok ok)
    {
        var probeRes = await tunnelProvider.ProbeAsync(ok.Value);
        return probeRes.Match(
            success => Results.Ok(new { success }),
            err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
        );
    }
    return res.Match(
        _ => Results.Problem("Unexpected success state"),
        err => Results.Problem(err.Match(t => t.Detail, p => p.Detail))
    );
});

await app.RunAsync();

public record EnqueueRequest(string WorkingDir, string Prompt, int Priority, int MaxAttempts, string[]? AllowedTools = null, string[]? DisallowedTools = null, string? ParentId = null);
