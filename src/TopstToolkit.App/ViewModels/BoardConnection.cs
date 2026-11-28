using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Board;
using TopstToolkit.Engine;

namespace TopstToolkit.App.ViewModels;

/// <summary>
/// The board the app is connected to (design §5.10): the SSH client, the runner on the board, and its status as it
/// changes. One per window, kept by the shell, so the header shows the board on every page and a run on the 보드 page
/// goes on while other pages show.
/// </summary>
public partial class BoardConnection : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private BoardMonitor? _monitor;

    public BoardConnection(AppServices services) => _services = services;

    public BoardTarget? Target { get; private set; }
    public SshClient? Ssh { get; private set; }
    public BoardAgent? Agent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillText), nameof(PillState))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillText), nameof(PillTemperature), nameof(Name))]
    private BoardStatus? _status;

    /// <summary>A run, a speed test or a long run is using the board's NPU.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillText), nameof(PillState))]
    private bool _isRunning;

    /// <summary>Why the connection ended when the app did not end it ("보드와 연결이 끊겼어요"), else empty.</summary>
    [ObservableProperty] private string _lostText = "";

    /// <summary>The temperature when a run started, for "45 → 49°C".</summary>
    public double? RunStartTemperature { get; private set; }

    public string Name => Status?.Host is { Length: > 0 } host ? host : Target?.Host ?? "";
    public string PillText => IsRunning ? $"{Name} 실행 중" : Name;
    public string PillTemperature => Status is { Temperatures.Length: > 0 } s ? $"{s.MaxTemperature:0}°C" : "";
    public StepState PillState => IsRunning ? StepState.Running : StepState.Ok;

    /// <summary>Raised on the UI thread for every status line.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The status lines of the last 15 minutes and what the app ran then (the graphs of 보드 › 상태).</summary>
    public BoardHistory History { get; } = new();

    /// <summary>The runner that comes with the app: engine\board\topst_runner.py.</summary>
    public static string? RunnerScript()
    {
        var helpers = EngineLayout.FindHelpersDir() ?? EngineLayout.FindHelpersDir(AppContext.BaseDirectory);
        if (helpers is null)
            return null;
        var script = BoardAgent.RunnerScriptIn(helpers);
        return File.Exists(script) ? script : null;
    }

    /// <summary>
    /// Logs in with the PC's key, puts the runner on the board when it is not the same version (design §5.10, /usr/bin),
    /// and starts the status stream. Throws <see cref="BoardException"/> with the reason in the screen's words.
    /// </summary>
    public async Task ConnectAsync(BoardTarget target, CancellationToken ct = default)
    {
        await DisconnectAsync();
        var script = RunnerScript() ?? throw new BoardException(BoardFailure.Command, "앱의 보드 실행기 파일이 없어요", "engine\\board\\topst_runner.py");
        var ssh = new SshClient(target);
        var agent = new BoardAgent(ssh, script);
        await agent.EnsureRunnerAsync(ct);

        var first = new TaskCompletionSource<BoardStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = BoardMonitor.Start(ssh, 2);
        monitor.Status += s =>
        {
            first.TrySetResult(s);
            _ui.BeginInvoke(() => OnStatus(monitor, s));
        };
        monitor.Stopped += e =>
        {
            first.TrySetException(e ?? new BoardException(BoardFailure.Unreachable, "보드와 연결이 끊겼어요"));
            _ui.BeginInvoke(() => OnStopped(monitor, e));
        };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            Status = await first.Task.WaitAsync(limit.Token);
        }
        catch (Exception)
        {
            await monitor.DisposeAsync();
            throw;
        }
        _monitor = monitor;
        Target = target;
        Ssh = ssh;
        Agent = agent;
        LostText = "";
        IsConnected = true;
        var settings = _services.Settings;
        settings.BoardHost = target.Host;
        settings.BoardUser = target.User;
        settings.BoardName = Status.Host;
        try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        StatusChanged?.Invoke(this, EventArgs.Empty);
        _ = UseBoardClocksAsync(agent);
    }

    /// <summary>
    /// The estimates of 6 성능 assume the TOPST AI-G board's clocks; a board that runs its NPU at others makes the next
    /// estimates use those (PerfClocks, design §11 2026-11-28). In the background: the connection does not wait for it.
    /// </summary>
    private async Task UseBoardClocksAsync(BoardAgent agent)
    {
        try
        {
            if (await agent.ReadClocksAsync() is { } clocks && _services.EngineLayout is { } layout)
                TopstToolkit.Core.Pipeline.PerfClocks.UseBoardClocks(clocks, layout, _services.Projects.Workspace);
        }
        catch (Exception e) when (e is BoardException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    public async Task DisconnectAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        IsConnected = false;
        IsRunning = false;
        History.EndSpan(DateTimeOffset.Now);
        if (monitor is not null)
            await monitor.DisposeAsync();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Marks the NPU in use by the app (pill "실행 중"), remembers the temperature at the start, and marks the stretch
    /// in the graphs of 보드 › 상태 with <paramref name="label"/> ("영상 · YOLOv8n").
    /// </summary>
    public void BeginRun(string label)
    {
        IsRunning = true;
        RunStartTemperature = Status?.MaxTemperature;
        History.BeginSpan(label, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EndRun()
    {
        IsRunning = false;
        History.EndSpan(DateTimeOffset.Now);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatus(BoardMonitor source, BoardStatus status)
    {
        if (!ReferenceEquals(source, _monitor) && _monitor is not null)
            return;
        Status = status;
        History.UseBoard(status.Host);
        History.Add(status, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(BoardMonitor source, BoardException? error)
    {
        if (!ReferenceEquals(source, _monitor) || error is null)
            return;
        _monitor = null;
        IsConnected = false;
        IsRunning = false;
        History.EndSpan(DateTimeOffset.Now);
        LostText = error.Message;
        _ = source.DisposeAsync();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
