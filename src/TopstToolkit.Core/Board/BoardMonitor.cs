using System.Diagnostics;
using System.Globalization;

namespace TopstToolkit.Core.Board;

/// <summary>
/// The board's status as it changes: <c>topst-runner monitor</c> over one SSH connection, a line every interval.
/// Events come on a worker thread. <see cref="Stopped"/> says why the stream ended (null: it was disposed).
/// </summary>
public sealed class BoardMonitor : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _reader;
    private volatile bool _disposed;

    private BoardMonitor(Process process)
    {
        _process = process;
        _reader = Task.Run(ReadAsync);
    }

    public event Action<BoardStatus>? Status;
    public event Action<BoardException?>? Stopped;

    public static BoardMonitor Start(SshClient ssh, double intervalSeconds = 2)
    {
        var process = ssh.Start($"{BoardAgent.RunnerPath} monitor --interval {intervalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}");
        process.StandardInput.Close();
        return new BoardMonitor(process);
    }

    private async Task ReadAsync()
    {
        var stderr = _process.StandardError.ReadToEndAsync();
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (BoardStatus.Parse(line) is { } status)
                    Status?.Invoke(status);
        }
        catch (IOException)
        {
        }
        if (_disposed)
        {
            Stopped?.Invoke(null);
            return;
        }
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        var text = await stderr.ConfigureAwait(false);
        Stopped?.Invoke(_process.ExitCode == 255 ? SshClient.Classify(text)
            : new BoardException(BoardFailure.Unreachable, "보드와 연결이 끊겼어요", text.Trim()));
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // a reader that failed has nothing more to say
        }
        _process.Dispose();
    }
}
