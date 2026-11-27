using System.Diagnostics;
using System.Text;

namespace TopstToolkit.Core.Board;

/// <summary>The board to talk to: <c>user@host</c> over SSH with the PC's key (design §5.10).</summary>
public sealed record BoardTarget(string Host, string User = "root", int Port = 22)
{
    public string Address => $"{User}@{Host}";
}

public sealed record SshResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Why talking to the board failed, in the terms the screen uses.</summary>
public enum BoardFailure
{
    /// <summary>No answer: wrong address, board off, or another network.</summary>
    Unreachable,
    /// <summary>The board answered but did not take this PC's key.</summary>
    KeyRefused,
    /// <summary>The board's own key is not the one this PC knew (re-installed board, or another device on that address).</summary>
    HostKeyChanged,
    /// <summary>Windows has no OpenSSH client.</summary>
    NoSsh,
    /// <summary>Connected, but the command on the board failed.</summary>
    Command,
}

public sealed class BoardException(BoardFailure failure, string message, string? detail = null) : Exception(message)
{
    public BoardFailure Failure { get; } = failure;
    /// <summary>What the board or ssh said, for the log.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>
/// The OpenSSH client that comes with Windows (<c>ssh.exe</c>, <c>scp.exe</c>), key login only: it never asks for a
/// password (BatchMode), takes a new board's key the first time (accept-new) and refuses a changed one.
/// </summary>
public sealed class SshClient
{
    private readonly string _ssh;
    private readonly string _scp;

    public SshClient(BoardTarget target, string? sshExe = null)
    {
        Target = target;
        _ssh = sshExe ?? FindSsh() ?? throw new BoardException(BoardFailure.NoSsh, "Windows에 SSH 클라이언트가 없어요",
            "OpenSSH client (ssh.exe) not found in System32\\OpenSSH or PATH");
        _scp = Path.Combine(Path.GetDirectoryName(_ssh)!, "scp.exe");
    }

    public BoardTarget Target { get; }

    /// <summary>The ssh.exe this client runs.</summary>
    public string SshPath => _ssh;

    /// <summary>Windows' OpenSSH client (an optional feature that is on by default since Windows 10 1809), or the first on PATH.</summary>
    public static string? FindSsh()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (File.Exists(system))
            return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "ssh.exe");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.Trim(), "scp.exe")))
                return candidate;
        }
        return null;
    }

    private IEnumerable<string> Options(int connectTimeoutSeconds = 8) =>
    [
        "-o", "BatchMode=yes", "-o", $"ConnectTimeout={connectTimeoutSeconds}", "-o", "StrictHostKeyChecking=accept-new",
        "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=3", "-o", "LogLevel=ERROR",
    ];

    private ProcessStartInfo SshStart(string command)
    {
        var psi = new ProcessStartInfo(_ssh)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var option in Options())
            psi.ArgumentList.Add(option);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(Target.Address);
        psi.ArgumentList.Add(command);
        return psi;
    }

    /// <summary>Runs one command on the board and waits for it.</summary>
    /// <exception cref="BoardException">ssh itself failed (exit 255): no connection, key refused, changed host key.</exception>
    public async Task<SshResult> RunAsync(string command, byte[]? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = SshStart(command) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
            await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            throw new BoardException(BoardFailure.Unreachable, "보드가 제때 답하지 않았어요", $"timed out: {command}");
        }
        var result = new SshResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        if (result.ExitCode == 255)
            throw Classify(result.StdErr);
        return result;
    }

    /// <summary>Runs a command and returns its output, or throws with the board's own words when it failed.</summary>
    public async Task<string> RunCheckedAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await RunAsync(command, null, timeout, ct).ConfigureAwait(false);
        if (!result.Ok)
            throw new BoardException(BoardFailure.Command, "보드에서 명령이 실패했어요", $"{command}\n{result.StdErr.Trim()}");
        return result.StdOut;
    }

    /// <summary>Copies files into a folder on the board (scp, key login, the same options as ssh).</summary>
    public async Task UploadAsync(IReadOnlyList<string> localFiles, string remoteDir, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_scp)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("-q");
        foreach (var option in Options())
            psi.ArgumentList.Add(option);
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(Target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var file in localFiles)
            psi.ArgumentList.Add(file);
        psi.ArgumentList.Add($"{Target.Address}:{remoteDir.TrimEnd('/')}/");
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        if (process.ExitCode != 0)
        {
            var text = await stderr.ConfigureAwait(false);
            throw process.ExitCode == 255 ? Classify(text) : new BoardException(BoardFailure.Command, "보드로 파일을 보내지 못했어요", text.Trim());
        }
    }

    /// <summary>Starts a long-running command whose stdin and stdout the caller drives (binary through <c>BaseStream</c>).</summary>
    public Process Start(string command)
    {
        var process = new Process { StartInfo = SshStart(command) };
        process.Start();
        return process;
    }

    /// <summary>What ssh printed when it failed (exit code 255), as a <see cref="BoardFailure"/>.</summary>
    public static BoardException Classify(string stderr)
    {
        var text = stderr.Trim();
        if (text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return new BoardException(BoardFailure.HostKeyChanged, "보드의 키가 전과 달라요", text);
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase))
            return new BoardException(BoardFailure.KeyRefused, "보드가 이 PC의 키를 받지 않았어요", text);
        return new BoardException(BoardFailure.Unreachable, "보드에 연결하지 못했어요", text);
    }

    /// <summary>A value as one word for the board's shell (single quotes).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }
}
