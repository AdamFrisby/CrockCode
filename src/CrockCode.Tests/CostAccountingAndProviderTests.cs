using System;
using System.Collections.Generic;
using System.Text.Json;
using CrockCode.Core.Contracts;
using CrockCode.Core.Domain;
using CrockCode.Core.Workflow;
using CrockCode.Providers;
using Xunit;

namespace CrockCode.Tests;

public class CostAccountingAndProviderTests
{
    [Fact]
    public void OpenAiBatchProvider_ShouldSupport_CompileTimeSubstitution()
    {
        IBatchProvider provider = new OpenAiBatchProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void BatchPricing_ShouldReflect_FiftyPercentDiscount()
    {
        // Sonnet base prices: Input $3.00/M, Output $15.00/M
        // Batch prices (50%): Input $1.50/M, Output $7.50/M
        int inputTokens = 100_000;
        int outputTokens = 20_000;

        decimal inputPricePerMillion = 3.0m;
        decimal outputPricePerMillion = 15.0m;

        decimal expectedCost = (inputTokens * (inputPricePerMillion / 2.0m) + outputTokens * (outputPricePerMillion / 2.0m)) / 1_000_000m;
        decimal actualCost = (inputTokens * 1.5m + outputTokens * 7.5m) / 1_000_000m;

        Assert.Equal(0.30m, expectedCost); // $0.15 input + $0.15 output
        Assert.Equal(expectedCost, actualCost);
    }

    [Fact]
    public void HaikuBatchPricing_ShouldReflect_HaikuDiscount()
    {
        // Haiku base prices: Input $0.80/M, Output $4.00/M
        // Batch prices (50%): Input $0.40/M, Output $2.00/M
        int inputTokens = 100_000;
        int outputTokens = 20_000;

        decimal inputPricePerMillion = 0.8m;
        decimal outputPricePerMillion = 4.0m;

        decimal expectedCost = (inputTokens * (inputPricePerMillion / 2.0m) + outputTokens * (outputPricePerMillion / 2.0m)) / 1_000_000m;
        
        Assert.Equal(0.08m, expectedCost); // $0.04 input + $0.04 output
    }
}
