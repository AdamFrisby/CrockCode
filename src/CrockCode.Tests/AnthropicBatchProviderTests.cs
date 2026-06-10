using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Providers;
using Xunit;

namespace CrockCode.Tests;

public class AnthropicBatchProviderTests
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

    private (AnthropicBatchProvider Provider, MockHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var provider = new AnthropicBatchProvider(httpClient, "mock-api-key");
        return (provider, handler);
    }

    [Fact]
    public async Task SubmitAsync_FreshSpec_Success_ReturnsWorkerHandle()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages/batches", req.RequestUri?.ToString());
            Assert.Equal("mock-api-key", req.Headers.GetValues("x-api-key").First());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "in_progress" })
            };
        };

        var spec = new WorkerSpec.Fresh(
            McpEndpoint: "http://mcp",
            AuthToken: "token",
            Model: "claude-3-5-sonnet",
            MaxToolTurns: 50,
            SystemPrompt: "Do logic"
        );

        var res = await provider.SubmitAsync(new WorkerId("wkr_1"), spec);

        Assert.True(res.IsOk);
        Assert.Equal("batch_123", res.Unwrap().BatchRef.Value);
    }

    [Fact]
    public async Task SubmitAsync_ResumeSpec_WithCheckpointAndInjectedResults_Success()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = "batch_resume_123", processing_status = "in_progress" })
        };

        var messagesBlob = JsonSerializer.Serialize(new List<object>
        {
            new { role = "user", content = "Hello" },
            new { role = "assistant", content = "Hi" }
        });

        var spec = new WorkerSpec.Resume(
            McpEndpoint: "http://mcp",
            AuthToken: "token",
            Model: "claude-3-5-sonnet",
            MaxToolTurns: 50,
            SystemPrompt: "Do logic",
            Checkpoint: new Checkpoint(messagesBlob, 100),
            InjectedResults: ImmutableArray.Create(
                new ChildResult(new TaskId("subtask_1"), new ResultSummary("Subtask 1 complete"))
            )
        );

        var res = await provider.SubmitAsync(new WorkerId("wkr_2"), spec);

        Assert.True(res.IsOk);
        Assert.Equal("batch_resume_123", res.Unwrap().BatchRef.Value);
    }

    [Fact]
    public async Task SubmitAsync_ResumeSpec_NoInjectedResults_Success()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = "batch_resume_empty", processing_status = "in_progress" })
        };

        var spec = new WorkerSpec.Resume(
            McpEndpoint: "http://mcp",
            AuthToken: "token",
            Model: "claude-3-5-sonnet",
            MaxToolTurns: 50,
            SystemPrompt: "Do logic",
            Checkpoint: new Checkpoint("", 0),
            InjectedResults: ImmutableArray<ChildResult>.Empty
        );

        var res = await provider.SubmitAsync(new WorkerId("wkr_3"), spec);

        Assert.True(res.IsOk);
        Assert.Equal("batch_resume_empty", res.Unwrap().BatchRef.Value);
    }

    [Fact]
    public async Task SubmitAsync_HttpError_ReturnsProviderError()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Invalid model name")
        };

        var spec = new WorkerSpec.Fresh("http://mcp", "token", "claude-invalid", 50, "Do logic");
        var res = await provider.SubmitAsync(new WorkerId("wkr_4"), spec);

        Assert.True(res.IsErr);
        Assert.Equal("PROVIDER_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task SubmitAsync_Exception_ReturnsNetworkError()
    {
        var (provider, handler) = CreateProvider();
        handler.ResponseFunc = req => throw new HttpRequestException("Timeout");

        var spec = new WorkerSpec.Fresh("http://mcp", "token", "claude-3-5-sonnet", 50, "Do logic");
        var res = await provider.SubmitAsync(new WorkerId("wkr_5"), spec);

        Assert.True(res.IsErr);
        Assert.Equal("NETWORK_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task PollAsync_InProgressOrCanceling_ReturnsInFlight()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = "batch_123", processing_status = "in_progress" })
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsOk);
        Assert.True(res.Unwrap().Status is WorkerStatus.InFlight);
    }

    [Fact]
    public async Task PollAsync_Ended_SucceededResult_CalculatesPriceCorrectly()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                // Return NDJSON line representing successful completion
                var resultItem = new
                {
                    custom_id = "wkr_1",
                    result = new
                    {
                        type = "succeeded",
                        message = new
                        {
                            model = "claude-3-5-sonnet-20241022",
                            usage = new
                            {
                                input_tokens = 1000000,
                                output_tokens = 1000000
                            }
                        }
                    }
                };
                string ndjson = JsonSerializer.Serialize(resultItem) + "\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ndjson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsOk);
        var outcome = res.Unwrap();
        Assert.True(outcome.Status is WorkerStatus.Succeeded);
        
        // Base pricing: input $3/million, output $15/million. Batch discount is 50%.
        // Cost = (1000000 * 1.5 + 1000000 * 7.5) / 1000000 = $9.00
        Assert.Equal(9.00m, outcome.Usage.CostUsd);
    }

    [Fact]
    public async Task PollAsync_Ended_HaikuModelPricing()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                var resultItem = new
                {
                    custom_id = "wkr_1",
                    result = new
                    {
                        type = "succeeded",
                        message = new
                        {
                            model = "claude-3-5-haiku",
                            usage = new
                            {
                                input_tokens = 1000000,
                                output_tokens = 1000000
                            }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resultItem) + "\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        var outcome = res.Unwrap();
        Assert.True(outcome.Status is WorkerStatus.Succeeded);
        
        // Haiku pricing: input $0.8/million, output $4/million. Batch discount 50%.
        // Cost = (1000000 * 0.4 + 1000000 * 2) / 1000000 = $2.40
        Assert.Equal(2.40m, outcome.Usage.CostUsd);
    }

    [Fact]
    public async Task PollAsync_Ended_OpusModelPricing()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                var resultItem = new
                {
                    custom_id = "wkr_1",
                    result = new
                    {
                        type = "succeeded",
                        message = new
                        {
                            model = "claude-3-opus",
                            usage = new
                            {
                                input_tokens = 1000000,
                                output_tokens = 1000000
                            }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resultItem) + "\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        var outcome = res.Unwrap();
        Assert.True(outcome.Status is WorkerStatus.Succeeded);
        
        // Opus pricing: input $15/million, output $75/million. Batch discount 50%.
        // Cost = (1000000 * 7.5 + 1000000 * 37.5) / 1000000 = $45.00
        Assert.Equal(45.00m, outcome.Usage.CostUsd);
    }

    [Fact]
    public async Task PollAsync_Ended_ExpiredResult_ReturnsExpired()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                var resultItem = new
                {
                    custom_id = "wkr_1",
                    result = new { type = "expired" }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resultItem) + "\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsOk);
        Assert.True(res.Unwrap().Status is WorkerStatus.Expired);
    }

    [Fact]
    public async Task PollAsync_Ended_ErroredResult_ReturnsErrored()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                var resultItem = new
                {
                    custom_id = "wkr_1",
                    result = new
                    {
                        type = "errored",
                        error = new { message = "Exceeded max tool turns" }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resultItem) + "\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsOk);
        var outcome = res.Unwrap();
        Assert.True(outcome.Status is WorkerStatus.Errored);
        var err = (WorkerStatus.Errored)outcome.Status;
        Assert.Equal("BATCH_FAILED", err.Error.Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task PollAsync_Ended_HarvestMissingItem_ReturnsError()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                // Different worker ID
                var resultItem = new
                {
                    custom_id = "wkr_other",
                    result = new { type = "succeeded" }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resultItem) + "\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsErr);
        Assert.Equal("HARVEST_MISSING_ITEM", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task PollAsync_Ended_HarvestHttpError_ReturnsError()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req =>
        {
            if (req.RequestUri?.ToString().EndsWith("/results") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "batch_123", processing_status = "ended" })
            };
        };

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsErr);
        Assert.Equal("PROVIDER_HARVEST_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task PollAsync_HttpError_ReturnsPollError()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.PollAsync(handle);

        Assert.True(res.IsErr);
        Assert.Equal("PROVIDER_POLL_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }

    [Fact]
    public async Task CancelAsync_Success_ReturnsTrue()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.OK);

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.CancelAsync(handle);

        Assert.True(res.IsOk);
        Assert.True(res.Unwrap());
    }

    [Fact]
    public async Task CancelAsync_HttpError_ReturnsFalse()
    {
        var (provider, handler) = CreateProvider();

        handler.ResponseFunc = req => new HttpResponseMessage(HttpStatusCode.Conflict);

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.CancelAsync(handle);

        Assert.True(res.IsOk);
        Assert.False(res.Unwrap());
    }

    [Fact]
    public async Task CancelAsync_Exception_ReturnsNetworkError()
    {
        var (provider, handler) = CreateProvider();
        handler.ResponseFunc = req => throw new HttpRequestException("Disconnect");

        var handle = new WorkerHandle(new WorkerId("wkr_1"), new ProviderBatchRef("batch_123"));
        var res = await provider.CancelAsync(handle);

        Assert.True(res.IsErr);
        Assert.Equal("NETWORK_ERROR", res.UnwrapErr().Match(t => t.Code, p => p.Code));
    }
}
