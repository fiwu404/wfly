using System.Diagnostics;
using WFly.Models;

namespace WFly.Services;

internal sealed class CoreProcessService : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private bool _disposed;

    public event Action<CoreLogEntry>? LogReceived;
    public event Action<bool>? RunningStateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Task StartAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException("已安装内核的可执行文件不存在。请重新下载内核。", fullExecutablePath);
        }

        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("已有内核正在运行。请先停止它。");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(fullExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, eventArgs) => PublishLine("OUT", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => PublishLine("ERR", eventArgs.Data);
        process.Exited += (_, _) => OnProcessExited(process);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("内核进程未能启动。");
            }

            lock (_sync)
            {
                _process = process;
            }

            if (process.HasExited)
            {
                OnProcessExited(process);
                return Task.CompletedTask;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            PublishLine("SYS", $"已启动：{Path.GetFileName(fullExecutablePath)}（PID {process.Id}）");
            RunningStateChanged?.Invoke(true);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        PublishLine("SYS", $"正在停止 PID {process.Id}…");
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }

        await process.WaitForExitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void OnProcessExited(Process process)
    {
        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        var changed = false;
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                changed = true;
            }
        }

        PublishLine("SYS", $"内核进程已退出（代码 {exitCode}）。");
        if (changed)
        {
            RunningStateChanged?.Invoke(false);
        }
    }

    private void PublishLine(string stream, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogReceived?.Invoke(new CoreLogEntry(DateTimeOffset.Now, stream, message));
    }
}
