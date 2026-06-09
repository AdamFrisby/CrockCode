using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;

namespace CrockCode.Providers;

public sealed class AnthropicBatchProvider : IBatchProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public AnthropicBatchProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "mcp-client-2025-11-20,message-batches-2024-09-24");
        if (content != null)
        {
            request.Content = content;
        }
        return request;
    }

    public async Task<Result<WorkerHandle>> SubmitAsync(WorkerId idemKey, WorkerSpec spec, CancellationToken ct = default)
    {
        try
        {
            string model = "";
            string systemPrompt = "";
            string mcpEndpoint = "";
            string authToken = "";
            var messages = new List<AnthropicMessage>();

            spec.Match(
                fresh =>
                {
                    model = fresh.Model;
                    systemPrompt = fresh.SystemPrompt;
                    mcpEndpoint = fresh.McpEndpoint;
                    authToken = fresh.AuthToken;
                    messages.Add(new AnthropicMessage 
                    { 
                        Role = "user", 
                        Content = "Please fetch the task from MCP and execute it." 
                    });
                    return Unit.Value;
                },
                resume =>
                {
                    model = resume.Model;
                    systemPrompt = resume.SystemPrompt;
                    mcpEndpoint = resume.McpEndpoint;
                    authToken = resume.AuthToken;

                    if (!string.IsNullOrEmpty(resume.Checkpoint.MessagesBlob))
                    {
                        var deserialized = JsonSerializer.Deserialize<List<AnthropicMessage>>(resume.Checkpoint.MessagesBlob);
                        if (deserialized != null)
                        {
                            messages.AddRange(deserialized);
                        }
                    }

                    if (resume.InjectedResults.Length > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("Awaited subagents completed:");
                        foreach (var res in resume.InjectedResults)
                        {
                            sb.AppendLine($"- Subtask {res.TaskId.Value} finished with outcome: {res.ResultSummary.Summary}");
                        }
                        messages.Add(new AnthropicMessage
                        {
                            Role = "user",
                            Content = sb.ToString()
                        });
                    }
                    else
                    {
                        messages.Add(new AnthropicMessage
                        {
                            Role = "user",
                            Content = "Continue working on the task."
                        });
                    }

                    return Unit.Value;
                });

            // Concatenate orchestration preamble with user prompt
            string preamble = 
                "You are a CrockCode worker. First call get_task (no arguments) to receive your task and working directory. " +
                "Then complete the task using your normal tools (Read, Edit, Write, Bash, Glob, Grep, ...) exactly as you always do. " +
                "If get_task returns {\"status\":\"no_task\"}, stop immediately. " +
                "When finished, call complete_task with a summary. " +
                "After a successful complete_task you may call get_task again to drain more work (up to your limit), otherwise stop.\n\n";

            string fullSystemPrompt = preamble + systemPrompt;

            var batchRequest = new AnthropicBatchRequest
            {
                Requests = new List<AnthropicBatchRequestItem>
                {
                    new()
                    {
                        CustomId = idemKey.Value,
                        Params = new AnthropicBatchParams
                        {
                            Model = model,
                            MaxTokens = 8000,
                            System = fullSystemPrompt,
                            Messages = messages,
                            McpServers = new List<AnthropicMcpServer>
                            {
                                new() { Url = mcpEndpoint, AuthorizationToken = authToken }
                            }
                        }
                    }
                }
            };

            var requestContent = JsonContent.Create(batchRequest);
            using var request = CreateRequest(HttpMethod.Post, "https://api.anthropic.com/v1/messages/batches", requestContent);
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<WorkerHandle>.Err(new Error.Permanent("PROVIDER_ERROR", $"API failed: {response.StatusCode} - {errMsg}"));
            }

            var batchResponse = await response.Content.ReadFromJsonAsync<AnthropicBatchResponse>(cancellationToken: ct);
            if (batchResponse == null || string.IsNullOrEmpty(batchResponse.Id))
            {
                return new Result<WorkerHandle>.Err(new Error.Permanent("PROVIDER_ERROR", "Invalid response from Anthropic API"));
            }

            return Result.Ok(new WorkerHandle(idemKey, new ProviderBatchRef(batchResponse.Id)));
        }
        catch (Exception ex)
        {
            return new Result<WorkerHandle>.Err(new Error.Transient("NETWORK_ERROR", ex.Message));
        }
    }

    public async Task<Result<WorkerOutcome>> PollAsync(WorkerHandle handle, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"https://api.anthropic.com/v1/messages/batches/{handle.BatchRef.Value}");
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errMsg = await response.Content.ReadAsStringAsync(ct);
                return new Result<WorkerOutcome>.Err(new Error.Transient("PROVIDER_POLL_ERROR", $"Poll failed: {response.StatusCode} - {errMsg}"));
            }

            var batchResponse = await response.Content.ReadFromJsonAsync<AnthropicBatchResponse>(cancellationToken: ct);
            if (batchResponse == null)
            {
                return new Result<WorkerOutcome>.Err(new Error.Permanent("PROVIDER_ERROR", "Invalid poll response from Anthropic API"));
            }

            if (batchResponse.ProcessingStatus is "in_progress" or "canceling")
            {
                return Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.InFlight()));
            }

            if (batchResponse.ProcessingStatus == "ended")
            {
                // Harvest results
                using var resultsRequest = CreateRequest(HttpMethod.Get, $"https://api.anthropic.com/v1/messages/batches/{handle.BatchRef.Value}/results");
                using var resultsResponse = await _httpClient.SendAsync(resultsRequest, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resultsResponse.IsSuccessStatusCode)
                {
                    string errMsg = await resultsResponse.Content.ReadAsStringAsync(ct);
                    return new Result<WorkerOutcome>.Err(new Error.Transient("PROVIDER_HARVEST_ERROR", $"Harvest failed: {resultsResponse.StatusCode} - {errMsg}"));
                }

                using var stream = await resultsResponse.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var item = JsonSerializer.Deserialize<AnthropicBatchResultItem>(line);
                    if (item != null && item.CustomId == handle.Id.Value)
                    {
                        var res = item.Result;
                        if (res.Type == "succeeded" && res.Message?.Usage != null)
                        {
                            int inTokens = res.Message.Usage.InputTokens;
                            int outTokens = res.Message.Usage.OutputTokens;
                            string modelName = res.Message.Model ?? "";

                            decimal inputPricePerMillion = 3.0m;
                            decimal outputPricePerMillion = 15.0m;

                            if (modelName.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                            {
                                inputPricePerMillion = 0.8m;
                                outputPricePerMillion = 4.0m;
                            }
                            else if (modelName.Contains("opus", StringComparison.OrdinalIgnoreCase))
                            {
                                inputPricePerMillion = 15.0m;
                                outputPricePerMillion = 75.0m;
                            }

                            // Calculate 50% discount price for batch API
                            decimal cost = (inTokens * (inputPricePerMillion / 2.0m) + outTokens * (outputPricePerMillion / 2.0m)) / 1_000_000m;
                            var usage = new Usage(inTokens, outTokens, cost);
                            return Result.Ok(new WorkerOutcome(handle.Id, usage, new WorkerStatus.Succeeded()));
                        }
                        else if (res.Type == "expired")
                        {
                            return Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.Expired()));
                        }
                        else
                        {
                            string errMsg = res.Error?.Message ?? "Batch execution errored on provider side";
                            return Result.Ok(new WorkerOutcome(handle.Id, Usage.Zero, new WorkerStatus.Errored(new Error.Permanent("BATCH_FAILED", errMsg))));
                        }
                    }
                }

                return new Result<WorkerOutcome>.Err(new Error.Permanent("HARVEST_MISSING_ITEM", $"Result item for custom_id {handle.Id.Value} not found in results"));
            }

            return new Result<WorkerOutcome>.Err(new Error.Permanent("UNKNOWN_BATCH_STATUS", $"Unknown processing status {batchResponse.ProcessingStatus}"));
        }
        catch (Exception ex)
        {
            return new Result<WorkerOutcome>.Err(new Error.Transient("NETWORK_ERROR", ex.Message));
        }
    }

    public async Task<Result<bool>> CancelAsync(WorkerHandle handle, CancellationToken ct = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, $"https://api.anthropic.com/v1/messages/batches/{handle.BatchRef.Value}/cancel");
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // If it is already cancelled or finished, it might return 400 or 409, which we can consider successful or benign
                return Result.Ok(false);
            }

            return Result.Ok(true);
        }
        catch (Exception ex)
        {
            return new Result<bool>.Err(new Error.Transient("NETWORK_ERROR", ex.Message));
        }
    }

    // ── Anthropic API DTOs ──────────────────────────────────────────────

    private class AnthropicBatchRequest
    {
        [JsonPropertyName("requests")]
        public List<AnthropicBatchRequestItem> Requests { get; set; } = new();
    }

    private class AnthropicBatchRequestItem
    {
        [JsonPropertyName("custom_id")]
        public string CustomId { get; set; } = "";

        [JsonPropertyName("params")]
        public AnthropicBatchParams Params { get; set; } = new();
    }

    private class AnthropicBatchParams
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 8000;

        [JsonPropertyName("system")]
        public string System { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<AnthropicMessage> Messages { get; set; } = new();

        [JsonPropertyName("mcp_servers")]
        public List<AnthropicMcpServer> McpServers { get; set; } = new();
    }

    private class AnthropicMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public object Content { get; set; } = "";
    }

    private class AnthropicMcpServer
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("authorization_token")]
        public string AuthorizationToken { get; set; } = "";
    }

    private class AnthropicBatchResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("processing_status")]
        public string ProcessingStatus { get; set; } = ""; // in_progress, ended, canceling
    }

    private class AnthropicBatchResultItem
    {
        [JsonPropertyName("custom_id")]
        public string CustomId { get; set; } = "";

        [JsonPropertyName("result")]
        public AnthropicBatchResult Result { get; set; } = new();
    }

    private class AnthropicBatchResult
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = ""; // succeeded, errored, canceled, expired

        [JsonPropertyName("message")]
        public AnthropicMessageResponse? Message { get; set; }

        [JsonPropertyName("error")]
        public AnthropicErrorResponse? Error { get; set; }
    }

    private class AnthropicMessageResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("usage")]
        public AnthropicUsage? Usage { get; set; }
    }

    private class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }

    private class AnthropicErrorResponse
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }
}
