using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrockCode.Core.Domain;

public sealed record CrockConfig
{
    [JsonPropertyName("anthropic_api_key")]
    public string AnthropicApiKey { get; init; } = "";

    [JsonPropertyName("mcp_public_url")]
    public string McpPublicUrl { get; init; } = "";

    [JsonPropertyName("tunnel_provider")]
    public string TunnelProvider { get; init; } = "cloudflared";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "anthropic";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "claude-3-5-sonnet-20241022";

    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; init; } = 4;

    [JsonPropertyName("warm_idle_buffer")]
    public int WarmIdleBuffer { get; init; } = 1;

    [JsonPropertyName("local_port")]
    public int LocalPort { get; init; } = 5000;

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; } = 3;

    [JsonPropertyName("max_tasks_per_worker")]
    public int MaxTasksPerWorker { get; init; } = 3;

    [JsonPropertyName("idle_timeout_seconds")]
    public int IdleTimeoutSeconds { get; init; } = 300;

    [JsonPropertyName("mcp_config")]
    public string McpConfig { get; init; } = "";

    public static CrockConfig Load(string? currentDir = null, IDictionary<string, string>? flagOverrides = null)
    {
        var config = new CrockConfig();

        // 1. Defaults are initialized above.

        // 2. Load User Config: ~/.crockcode/config.json
        var homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crockcode");
        var userConfigPath = Path.Combine(homeDir, "config.json");
        config = MergeWithFile(config, userConfigPath);

        // 3. Load Project Config: ./.crockcode/config.json
        var activeDir = currentDir ?? Directory.GetCurrentDirectory();
        var projConfigPath = Path.Combine(activeDir, ".crockcode", "config.json");
        config = MergeWithFile(config, projConfigPath);

        // 4. Load Environment Variables
        config = MergeWithEnv(config);

        // 5. Load CLI flag overrides
        config = MergeWithFlags(config, flagOverrides);

        return config;
    }

    private static CrockConfig MergeWithFile(CrockConfig baseConfig, string path)
    {
        if (!File.Exists(path)) return baseConfig;
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            
            return new CrockConfig
            {
                AnthropicApiKey = GetStringProperty(parsed, "anthropic_api_key", baseConfig.AnthropicApiKey),
                McpPublicUrl = GetStringProperty(parsed, "mcp_public_url", baseConfig.McpPublicUrl),
                TunnelProvider = GetStringProperty(parsed, "tunnel_provider", baseConfig.TunnelProvider),
                Provider = GetStringProperty(parsed, "provider", baseConfig.Provider),
                Model = GetStringProperty(parsed, "model", baseConfig.Model),
                MaxConcurrency = GetIntProperty(parsed, "max_concurrency", baseConfig.MaxConcurrency),
                WarmIdleBuffer = GetIntProperty(parsed, "warm_idle_buffer", baseConfig.WarmIdleBuffer),
                LocalPort = GetIntProperty(parsed, "local_port", baseConfig.LocalPort),
                MaxAttempts = GetIntProperty(parsed, "max_attempts", baseConfig.MaxAttempts),
                MaxTasksPerWorker = GetIntProperty(parsed, "max_tasks_per_worker", baseConfig.MaxTasksPerWorker),
                IdleTimeoutSeconds = GetIntProperty(parsed, "idle_timeout_seconds", baseConfig.IdleTimeoutSeconds),
                McpConfig = GetStringProperty(parsed, "mcp_config", baseConfig.McpConfig)
            };
        }
        catch
        {
            return baseConfig;
        }
    }

    private static CrockConfig MergeWithEnv(CrockConfig baseConfig)
    {
        return new CrockConfig
        {
            AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? baseConfig.AnthropicApiKey,
            McpPublicUrl = Environment.GetEnvironmentVariable("CROCK_MCP_PUBLIC_URL") ?? baseConfig.McpPublicUrl,
            TunnelProvider = Environment.GetEnvironmentVariable("CROCK_TUNNEL_PROVIDER") ?? baseConfig.TunnelProvider,
            Provider = Environment.GetEnvironmentVariable("CROCK_PROVIDER") ?? baseConfig.Provider,
            Model = Environment.GetEnvironmentVariable("CROCK_MODEL") ?? baseConfig.Model,
            MaxConcurrency = GetEnvInt("CROCK_MAX_CONCURRENCY", baseConfig.MaxConcurrency),
            WarmIdleBuffer = GetEnvInt("CROCK_WARM_IDLE_BUFFER", baseConfig.WarmIdleBuffer),
            LocalPort = GetEnvInt("CROCK_LOCAL_PORT", baseConfig.LocalPort),
            MaxAttempts = GetEnvInt("CROCK_MAX_ATTEMPTS", baseConfig.MaxAttempts),
            MaxTasksPerWorker = GetEnvInt("CROCK_MAX_TASKS_PER_WORKER", baseConfig.MaxTasksPerWorker),
            IdleTimeoutSeconds = GetEnvInt("CROCK_IDLE_TIMEOUT_SECONDS", baseConfig.IdleTimeoutSeconds),
            McpConfig = Environment.GetEnvironmentVariable("CROCK_MCP_CONFIG") ?? baseConfig.McpConfig
        };
    }

    private static CrockConfig MergeWithFlags(CrockConfig baseConfig, IDictionary<string, string>? flags)
    {
        if (flags == null) return baseConfig;

        return new CrockConfig
        {
            AnthropicApiKey = flags.TryGetValue("anthropic_api_key", out var key) ? key : baseConfig.AnthropicApiKey,
            McpPublicUrl = flags.TryGetValue("mcp_public_url", out var url) ? url : baseConfig.McpPublicUrl,
            TunnelProvider = flags.TryGetValue("tunnel_provider", out var provider) ? provider : baseConfig.TunnelProvider,
            Provider = flags.TryGetValue("provider", out var prov) ? prov : baseConfig.Provider,
            Model = flags.TryGetValue("model", out var model) ? model : baseConfig.Model,
            MaxConcurrency = flags.TryGetValue("max_concurrency", out var mc) && int.TryParse(mc, out var mcVal) ? mcVal : baseConfig.MaxConcurrency,
            WarmIdleBuffer = flags.TryGetValue("warm_idle_buffer", out var wib) && int.TryParse(wib, out var wibVal) ? wibVal : baseConfig.WarmIdleBuffer,
            LocalPort = flags.TryGetValue("local_port", out var lp) && int.TryParse(lp, out var lpVal) ? lpVal : baseConfig.LocalPort,
            MaxAttempts = flags.TryGetValue("max_attempts", out var ma) && int.TryParse(ma, out var maVal) ? maVal : baseConfig.MaxAttempts,
            MaxTasksPerWorker = flags.TryGetValue("max_tasks_per_worker", out var mtw) && int.TryParse(mtw, out var mtwVal) ? mtwVal : baseConfig.MaxTasksPerWorker,
            IdleTimeoutSeconds = flags.TryGetValue("idle_timeout_seconds", out var its) && int.TryParse(its, out var itsVal) ? itsVal : baseConfig.IdleTimeoutSeconds,
            McpConfig = flags.TryGetValue("mcp_config", out var mcpCfg) ? mcpCfg : baseConfig.McpConfig
        };
    }

    private static string GetStringProperty(JsonElement elem, string propName, string defaultVal)
    {
        if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? defaultVal;
        }
        return defaultVal;
    }

    private static int GetIntProperty(JsonElement elem, string propName, int defaultVal)
    {
        if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            {
                return val;
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var sVal))
            {
                return sVal;
            }
        }
        return defaultVal;
    }

    private static int GetEnvInt(string envVar, int defaultVal)
    {
        var val = Environment.GetEnvironmentVariable(envVar);
        if (int.TryParse(val, out var parsed))
        {
            return parsed;
        }
        return defaultVal;
    }
}
