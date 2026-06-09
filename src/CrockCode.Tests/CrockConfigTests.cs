using System;
using System.Collections.Generic;
using System.IO;
using CrockCode.Core.Domain;
using Xunit;

namespace CrockCode.Tests;

public class CrockConfigTests : IDisposable
{
    private readonly string _tempDir;

    public CrockConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crock_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    [Fact]
    public void Config_ShouldInitialize_WithDefaultValues()
    {
        var config = new CrockConfig();

        Assert.Equal("anthropic", config.Provider);
        Assert.Equal("claude-3-5-sonnet-20241022", config.Model);
        Assert.Equal(4, config.MaxConcurrency);
        Assert.Equal(1, config.WarmIdleBuffer);
        Assert.Equal(5000, config.LocalPort);
        Assert.Equal(3, config.MaxAttempts);
        Assert.Equal(300, config.IdleTimeoutSeconds);
    }

    [Fact]
    public void MergeWithFlags_ShouldOverride_DefaultProperties()
    {
        var overrides = new Dictionary<string, string>
        {
            { "provider", "openai" },
            { "model", "gpt-4o" },
            { "max_concurrency", "10" },
            { "idle_timeout_seconds", "60" }
        };

        var config = CrockConfig.Load(currentDir: _tempDir, flagOverrides: overrides);

        Assert.Equal("openai", config.Provider);
        Assert.Equal("gpt-4o", config.Model);
        Assert.Equal(10, config.MaxConcurrency);
        Assert.Equal(60, config.IdleTimeoutSeconds);
    }

    [Fact]
    public void MergeWithEnv_ShouldOverride_DefaultProperties()
    {
        Environment.SetEnvironmentVariable("CROCK_PROVIDER", "custom-provider");
        Environment.SetEnvironmentVariable("CROCK_MODEL", "custom-model");
        Environment.SetEnvironmentVariable("CROCK_MAX_CONCURRENCY", "8");
        Environment.SetEnvironmentVariable("CROCK_IDLE_TIMEOUT_SECONDS", "120");

        try
        {
            var config = CrockConfig.Load(currentDir: _tempDir);

            Assert.Equal("custom-provider", config.Provider);
            Assert.Equal("custom-model", config.Model);
            Assert.Equal(8, config.MaxConcurrency);
            Assert.Equal(120, config.IdleTimeoutSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CROCK_PROVIDER", null);
            Environment.SetEnvironmentVariable("CROCK_MODEL", null);
            Environment.SetEnvironmentVariable("CROCK_MAX_CONCURRENCY", null);
            Environment.SetEnvironmentVariable("CROCK_IDLE_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public void Precedence_ShouldPrefer_Flags_Over_Env_Over_Defaults()
    {
        Environment.SetEnvironmentVariable("CROCK_MODEL", "env-model");
        var flags = new Dictionary<string, string> { { "model", "flag-model" } };

        try
        {
            var config = CrockConfig.Load(currentDir: _tempDir, flagOverrides: flags);
            Assert.Equal("flag-model", config.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CROCK_MODEL", null);
        }
    }

    [Fact]
    public void MergeWithFile_ShouldCorrectlyParse_JsonConfig()
    {
        var dotCrockcodeDir = Path.Combine(_tempDir, ".crockcode");
        Directory.CreateDirectory(dotCrockcodeDir);

        var json = @"{
            ""provider"": ""json-provider"",
            ""model"": ""json-model"",
            ""max_concurrency"": 5,
            ""idle_timeout_seconds"": 400
        }";
        File.WriteAllText(Path.Combine(dotCrockcodeDir, "config.json"), json);

        var config = CrockConfig.Load(currentDir: _tempDir);

        Assert.Equal("json-provider", config.Provider);
        Assert.Equal("json-model", config.Model);
        Assert.Equal(5, config.MaxConcurrency);
        Assert.Equal(400, config.IdleTimeoutSeconds);
    }
}
