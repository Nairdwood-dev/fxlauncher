using System.Diagnostics;
using System.IO;
using System.Text;
using Nairdwood.Launcher.Models;

namespace Nairdwood.Launcher.Services;

public enum ConsoleStream
{
    Standard,
    Error,
    Launcher
}

public sealed class ConsoleOutputEventArgs : EventArgs
{
    public ConsoleOutputEventArgs(string text, ConsoleStream stream)
    {
        Text = text;
        Stream = stream;
    }

    public string Text { get; }
    public ConsoleStream Stream { get; }
}

public sealed class ProcessExitedEventArgs : EventArgs
{
    public ProcessExitedEventArgs(int exitCode, bool expected)
    {
        ExitCode = exitCode;
        Expected = expected;
    }

    public int ExitCode { get; }
    public bool Expected { get; }
}

public sealed class ConsoleProcessHost : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private bool _expectedExit;
    private bool _disposed;

    public event EventHandler<ConsoleOutputEventArgs>? OutputReceived;
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false };
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false } process ? process.Id : null;
        }
    }

    public void Start(LauncherSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) throw new InvalidOperationException("A process is already running.");
        if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !File.Exists(settings.ExecutablePath))
            throw new FileNotFoundException("Select a valid FXServer executable or launch script.", settings.ExecutablePath);

        var startInfo = BuildStartInfo(settings);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnProcessExited;

        lock (_sync)
        {
            _expectedExit = false;
            _process = process;
        }

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows refused to start the selected process.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Emit($"Started PID {process.Id}: {settings.ExecutablePath}", ConsoleStream.Launcher);
        }
        catch
        {
            lock (_sync) _process = null;
            process.Dispose();
            throw;
        }
    }

    public void SendCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        Process? process;
        lock (_sync) process = _process;

        if (process is null || process.HasExited)
            throw new InvalidOperationException("The server is not running.");

        process.StandardInput.WriteLine(command);
        process.StandardInput.Flush();
        Emit($"> {command}", ConsoleStream.Launcher);
    }

    public async Task StopAsync(bool force, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _expectedExit = true;
        }

        if (process is null || process.HasExited) return;

        if (!force)
        {
            Emit("Requesting a graceful shutdown...", ConsoleStream.Launcher);
            try
            {
                process.StandardInput.WriteLine("quit");
                process.StandardInput.Flush();
            }
            catch
            {
                // The process may close its input before the exit event arrives.
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                FinalizeExitedProcess(process);
                return;
            }
            catch (InvalidOperationException) when (!IsCurrentProcess(process))
            {
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Emit("Graceful shutdown timed out; stopping the process tree.", ConsoleStream.Launcher);
            }
        }

        if (!IsCurrentProcess(process)) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            FinalizeExitedProcess(process);
        }
        catch (InvalidOperationException) when (!IsCurrentProcess(process))
        {
            // The Exited event won the race and already finalized this exact process.
        }
    }

    private static ProcessStartInfo BuildStartInfo(LauncherSettings settings)
    {
        var selectedPath = Path.GetFullPath(settings.ExecutablePath);
        var workingDirectory = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
            ? Path.GetDirectoryName(selectedPath)!
            : Path.GetFullPath(settings.WorkingDirectory);

        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");

        var extension = Path.GetExtension(selectedPath).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (extension is ".bat" or ".cmd")
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"call \"{selectedPath}\" {settings.Arguments}".TrimEnd());
        }
        else
        {
            startInfo.FileName = selectedPath;
            startInfo.Arguments = settings.Arguments;
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        return startInfo;
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null) Emit(args.Data, ConsoleStream.Standard);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not null) Emit(args.Data, ConsoleStream.Error);
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is Process process) FinalizeExitedProcess(process);
    }

    private bool IsCurrentProcess(Process process)
    {
        lock (_sync) return ReferenceEquals(_process, process);
    }

    private void FinalizeExitedProcess(Process process)
    {
        bool expected;
        lock (_sync)
        {
            if (!ReferenceEquals(_process, process)) return;
            expected = _expectedExit;
            _process = null;
        }

        var exitCode = process.ExitCode;
        process.OutputDataReceived -= OnOutputDataReceived;
        process.ErrorDataReceived -= OnErrorDataReceived;
        process.Exited -= OnProcessExited;
        process.Dispose();

        Emit($"Process exited with code {exitCode}.", exitCode == 0 ? ConsoleStream.Launcher : ConsoleStream.Error);
        ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode, expected));
    }

    private void Emit(string text, ConsoleStream stream) =>
        OutputReceived?.Invoke(this, new ConsoleOutputEventArgs(text, stream));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Process? process;
        lock (_sync) process = _process;
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        process?.Dispose();
    }
}
