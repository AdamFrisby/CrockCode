using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace CrockCode.McpServer;

public sealed class BackgroundProcess
{
    public required string Id { get; init; }
    public required string Command { get; init; }
    public required Process Process { get; init; }
    public StringBuilder Output { get; } = new();
    public StringBuilder Error { get; } = new();
    public bool IsCompleted { get; set; }
    public int? ExitCode { get; set; }
}

public sealed class BackgroundProcessManager
{
    private readonly ConcurrentDictionary<string, BackgroundProcess> _processes = new();
    private int _counter;

    public string Start(string command, string workingDir)
    {
        int idNum = System.Threading.Interlocked.Increment(ref _counter);
        string id = $"bg_task_{idNum}";

        var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.WorkingDirectory = workingDir;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        var bgProcess = new BackgroundProcess
        {
            Id = id,
            Command = command,
            Process = process
        };

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                lock (bgProcess.Output)
                {
                    bgProcess.Output.AppendLine(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                lock (bgProcess.Error)
                {
                    bgProcess.Error.AppendLine(e.Data);
                }
            }
        };

        process.EnableRaisingEvents = true;
        process.Exited += (s, e) =>
        {
            bgProcess.IsCompleted = true;
            try
            {
                bgProcess.ExitCode = process.ExitCode;
            }
            catch
            {
                // Process might already be disposed
            }
        };

        _processes[id] = bgProcess;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Write command and close standard input so it runs and completes
        process.StandardInput.WriteLine(command);
        process.StandardInput.Close();

        return id;
    }

    public BackgroundProcess? Get(string id)
    {
        _processes.TryGetValue(id, out var bgProcess);
        return bgProcess;
    }
}
