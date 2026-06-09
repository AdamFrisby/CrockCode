using System.Net.Http.Json;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Cli;

public sealed class HttpControlChannel : IControlChannel
{
    private readonly HttpClient _httpClient;

    public HttpControlChannel(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Result<TaskId>> EnqueueTaskAsync(
        WorkingDir workingDir, string prompt, Priority priority, int maxAttempts,
        ImmutableArray<string> allowedTools, ImmutableArray<string> disallowedTools,
        TaskId? parentId = null,
        CancellationToken ct = default)
    {
        try
        {
            var allowedArray = allowedTools.IsDefaultOrEmpty ? null : allowedTools.ToArray();
            var disallowedArray = disallowedTools.IsDefaultOrEmpty ? null : disallowedTools.ToArray();
            var req = new EnqueueRequest(workingDir.Value, prompt, priority.Value, maxAttempts, allowedArray, disallowedArray, parentId?.Value);
            var response = await _httpClient.PostAsJsonAsync("/api/tasks", req, ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<TaskId>.Err(new Error.Permanent("CLI_ERROR", $"Failed to enqueue: {response.StatusCode} - {errMsg}"));
            }

            var result = await response.Content.ReadFromJsonAsync<EnqueueResponse>(cancellationToken: ct);
            if (result == null || string.IsNullOrEmpty(result.TaskId))
            {
                return new Result<TaskId>.Err(new Error.Permanent("CLI_ERROR", "Invalid response from daemon"));
            }

            return Result.Ok(new TaskId(result.TaskId));
        }
        catch (Exception ex)
        {
            return new Result<TaskId>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    public async Task<Result<bool>> CancelTaskAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"/api/tasks/{taskId.Value}", ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<bool>.Err(new Error.Permanent("CLI_ERROR", $"Failed to cancel: {response.StatusCode} - {errMsg}"));
            }

            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    public async Task<Result<WorkflowState>> GetTaskStateAsync(TaskId taskId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/tasks/{taskId.Value}", ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<WorkflowState>.Err(new Error.Permanent("CLI_ERROR", $"Failed to get state: {response.StatusCode} - {errMsg}"));
            }

            var state = await response.Content.ReadFromJsonAsync<WorkflowState>(cancellationToken: ct);
            if (state == null)
            {
                return new Result<WorkflowState>.Err(new Error.Permanent("CLI_ERROR", "Invalid state payload from daemon"));
            }

            return Result.Ok(state);
        }
        catch (Exception ex)
        {
            return new Result<WorkflowState>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    public async Task<Result<ImmutableArray<WorkflowState>>> ListTasksAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tasks", ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<ImmutableArray<WorkflowState>>.Err(new Error.Permanent("CLI_ERROR", $"Failed to list tasks: {response.StatusCode} - {errMsg}"));
            }

            var tasks = await response.Content.ReadFromJsonAsync<ImmutableArray<WorkflowState>>(cancellationToken: ct);
            return Result.Ok(tasks.IsDefault ? ImmutableArray<WorkflowState>.Empty : tasks);
        }
        catch (Exception ex)
        {
            return new Result<ImmutableArray<WorkflowState>>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    public async IAsyncEnumerable<StreamEnvelope> FollowStreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("data: "))
            {
                var json = line.Substring(6).Trim();
                if (string.IsNullOrEmpty(json)) continue;

                StreamEnvelope? envelope = null;
                try
                {
                    envelope = JsonSerializer.Deserialize<StreamEnvelope>(json, options);
                }
                catch
                {
                    // ignore malformed JSON
                }

                if (envelope != null)
                {
                    yield return envelope;
                }
            }
        }
    }

    public async Task<Result<string>> GetTunnelUrlAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tunnel", ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<string>.Err(new Error.Permanent("CLI_ERROR", $"Failed to get tunnel URL: {response.StatusCode} - {errMsg}"));
            }

            var result = await response.Content.ReadFromJsonAsync<TunnelUrlResponse>(cancellationToken: ct);
            if (result == null || string.IsNullOrEmpty(result.Url))
            {
                return new Result<string>.Err(new Error.Permanent("CLI_ERROR", "Invalid response from daemon"));
            }

            return Result.Ok(result.Url);
        }
        catch (Exception ex)
        {
            return new Result<string>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    public async Task<Result<bool>> ProbeTunnelAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsync("/api/tunnel/probe", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<bool>.Err(new Error.Permanent("CLI_ERROR", $"Tunnel probe failed: {response.StatusCode} - {errMsg}"));
            }

            var result = await response.Content.ReadFromJsonAsync<TunnelProbeResponse>(cancellationToken: ct);
            if (result == null)
            {
                return new Result<bool>.Err(new Error.Permanent("CLI_ERROR", "Invalid response from daemon"));
            }

            return Result.Ok(result.Success);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Transient("CONNECTION_ERROR", $"Could not connect to daemon: {ex.Message}"));
        }
    }

    private record EnqueueRequest(string WorkingDir, string Prompt, int Priority, int MaxAttempts, string[]? AllowedTools = null, string[]? DisallowedTools = null, string? ParentId = null);
    private record EnqueueResponse(string TaskId);
    private record TunnelUrlResponse(string Url);
    private record TunnelProbeResponse(bool Success);
}
