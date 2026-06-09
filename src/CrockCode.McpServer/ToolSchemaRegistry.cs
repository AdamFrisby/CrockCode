using System.Collections.Generic;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace CrockCode.McpServer;

public static class ToolSchemaRegistry
{
    private static readonly Tool ReadTool = new()
    {
        Name = "Read",
        Description = "Read the contents of a file from the workspace.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""file_path"": { ""type"": ""string"", ""description"": ""The path to the file relative to the working directory."" },
                ""offset"": { ""type"": ""integer"", ""description"": ""Optional. Line offset (1-indexed) to start reading from."" },
                ""limit"": { ""type"": ""integer"", ""description"": ""Optional. Maximum number of lines to read."" }
            },
            ""required"": [ ""file_path"" ]
        }").RootElement
    };

    private static readonly Tool WriteTool = new()
    {
        Name = "Write",
        Description = "Write content to a file, replacing it entirely if it already exists.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""file_path"": { ""type"": ""string"", ""description"": ""The path to the file relative to the working directory."" },
                ""content"": { ""type"": ""string"", ""description"": ""The content to write to the file."" }
            },
            ""required"": [ ""file_path"", ""content"" ]
        }").RootElement
    };

    private static readonly Tool EditTool = new()
    {
        Name = "Edit",
        Description = "Edit an existing file by replacing a single exact match of old_string with new_string.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""file_path"": { ""type"": ""string"", ""description"": ""The path to the file relative to the working directory."" },
                ""old_string"": { ""type"": ""string"", ""description"": ""The exact string block in the file to replace."" },
                ""new_string"": { ""type"": ""string"", ""description"": ""The replacement string block."" }
            },
            ""required"": [ ""file_path"", ""old_string"", ""new_string"" ]
        }").RootElement
    };

    private static readonly Tool GlobTool = new()
    {
        Name = "Glob",
        Description = "Find files matching a glob pattern (e.g. **/*.cs).",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""pattern"": { ""type"": ""string"", ""description"": ""The pattern to search for."" },
                ""path"": { ""type"": ""string"", ""description"": ""Optional. Directory to search in, relative to working directory."" }
            },
            ""required"": [ ""pattern"" ]
        }").RootElement
    };

    private static readonly Tool GrepTool = new()
    {
        Name = "Grep",
        Description = "Search for a text pattern recursively in files.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""pattern"": { ""type"": ""string"", ""description"": ""The search query pattern."" },
                ""path"": { ""type"": ""string"", ""description"": ""Optional. Path relative to working directory to search."" }
            },
            ""required"": [ ""pattern"" ]
        }").RootElement
    };

    private static readonly Tool BashTool = new()
    {
        Name = "Bash",
        Description = "Execute a command in bash inside the working directory. Timeout is capped at 50 seconds.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""command"": { ""type"": ""string"", ""description"": ""The bash command to execute."" },
                ""timeout"": { ""type"": ""integer"", ""description"": ""Optional. Timeout in seconds (default 50)."" },
                ""run_in_background"": { ""type"": ""boolean"", ""description"": ""Optional. If true, run in background detached."" },
                ""description"": { ""type"": ""string"", ""description"": ""Optional description of command."" }
            },
            ""required"": [ ""command"" ]
        }").RootElement
    };

    private static readonly Tool BashOutputTool = new()
    {
        Name = "BashOutput",
        Description = "Retrieve output from a background bash task.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""bash_id"": { ""type"": ""string"", ""description"": ""The background task ID."" },
                ""filter"": { ""type"": ""string"", ""description"": ""Optional filter."" }
            },
            ""required"": [ ""bash_id"" ]
        }").RootElement
    };

    private static readonly Tool GetTaskTool = new()
    {
        Name = "get_task",
        Description = "Get the task details and working directory assigned to this worker. Call this first.",
        InputSchema = JsonDocument.Parse(@"{ ""type"": ""object"", ""properties"": {} }").RootElement
    };

    private static readonly Tool CompleteTaskTool = new()
    {
        Name = "complete_task",
        Description = "Mark the assigned task as complete and report results.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""summary"": { ""type"": ""string"", ""description"": ""The summary of changes made and results."" },
                ""status"": { ""type"": ""string"", ""description"": ""Optional. Success or Failure."" }
            },
            ""required"": [ ""summary"" ]
        }").RootElement
    };

    private static readonly Tool TaskTool = new()
    {
        Name = "Task",
        Description = "Asynchronously spawn a child subagent task.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""description"": { ""type"": ""string"", ""description"": ""Description of the sub-task."" },
                ""prompt"": { ""type"": ""string"", ""description"": ""Prompt instructing the subagent what to do."" },
                ""subagent_type"": { ""type"": ""string"", ""description"": ""Optional subagent type."" }
            },
            ""required"": [ ""description"", ""prompt"" ]
        }").RootElement
    };

    private static readonly Tool AwaitTool = new()
    {
        Name = "await",
        Description = "Suspend execution and wait for specific child subagent tasks to complete.",
        InputSchema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""handles"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Array of task IDs to wait for.""
                }
            },
            ""required"": [ ""handles"" ]
        }").RootElement
    };

    public static List<Tool> GetToolsForModel(string? model)
    {
        var list = new List<Tool>
        {
            ReadTool, WriteTool, EditTool, GlobTool, GrepTool, BashTool, BashOutputTool,
            GetTaskTool, CompleteTaskTool, TaskTool, AwaitTool
        };

        // If specific model demands customizations (like renaming 'content' to 'file_contents' or stripping tools),
        // we can clone/reshape the tools here. By default we return the reference mapping.
        return list;
    }
}
