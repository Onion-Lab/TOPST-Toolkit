using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Board;

namespace TopstToolkit.App.ViewModels;

/// <summary>A name, a bar and a value: CPU cores, NPUs, memory, temperatures (docs/mockups/mockup-board.html).</summary>
public sealed record MeterRow(string Name, double Fraction, string Value, string Unit = "", bool Muted = false);

/// <summary>A device the scan found on the PC's network.</summary>
public partial class BoardFoundRow : ObservableObject
{
    public required string Address { get; init; }
    public string? Name { get; init; }
    public bool IsTopst { get; init; }
    public string Title => Name ?? Address;
    public string Caption => IsTopst ? $"{Address} · TOPST AI-G" : Name is null ? "SSH 기기" : $"{Address} · SSH 기기";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 도구 › 보드 (design §5.10, docs/mockups/mockup-board.html): connect to the board over SSH, watch its status, run the
/// project's model on it with pictures or a video, and look after what the app put there. Layout-independent.
/// </summary>
public partial class BoardViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _scan;
    private bool _scannedOnce;

    public BoardViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        Board = shell.Board;
        Run = new BoardRunViewModel(services, shell, this);
        Manage = new BoardManageViewModel(services, shell, this);
        _host = services.Settings.BoardHost ?? "";
        _user = services.Settings.BoardUser is { Length: > 0 } user ? user : "root";
        Board.StatusChanged += (_, _) => OnBoardChanged();
        OnBoardChanged();
    }

    public BoardConnection Board { get; }
    public BoardRunViewModel Run { get; }
    public BoardManageViewModel Manage { get; }

    public void OnShown()
    {
        OnBoardChanged();
        if (!Board.IsConnected && !_scannedOnce)
            _ = ScanAsync();
        if (Board.IsConnected)
        {
            Run.OnShown();
            if (IsManageTab)
                _ = Manage.RefreshAsync();
        }
    }

    // ---------------------------------------------------------------- connect
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _user;
    [ObservableProperty] private bool _isKeyMode = true;
    [ObservableProperty] private bool _isPasswordMode;

    /// <summary>The board's password for the first connection; set by the page's PasswordBox, never kept.</summary>
    public string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty] private string _connectError = "";
    [ObservableProperty] private string _connectHelp = "";
    [ObservableProperty] private bool _canForgetHostKey;
    [ObservableProperty] private string _connectProgress = "";

    public ObservableCollection<BoardFoundRow> Found { get; } = [];
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "";

    [RelayCommand]
    private async Task ScanAsync()
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        var ct = _scan.Token;
        _scannedOnce = true;
        IsScanning = true;
        ScanText = "같은 네트워크에서 찾는 중…";
        try
        {
            var found = await Task.Run(() => BoardScanner.ScanAsync(22, ct), ct);
            Found.Clear();
            // TOPST boards first; other SSH devices only when no board looks like one
            var boards = found.Where(f => f.LooksLikeTopst).ToList();
            foreach (var c in boards.Count > 0 ? boards : found.Take(6).ToList())
                Found.Add(new BoardFoundRow { Address = c.Address, Name = c.Name, IsTopst = c.LooksLikeTopst });
            var pick = Found.FirstOrDefault(f => f.Address == Host) ?? (string.IsNullOrWhiteSpace(Host) ? Found.FirstOrDefault(f => f.IsTopst) : null);
            if (pick is not null)
                PickFound(pick);
            ScanText = found.Count == 0 ? "찾지 못했어요. 보드 주소를 직접 넣어 주세요." : boards.Count == 0 ? "TOPST 보드로 보이는 기기가 없어요." : "";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsScanning = false;
        }
    }

    [RelayCommand]
    private void PickFound(BoardFoundRow? row)
    {
        if (row is null)
            return;
        foreach (var f in Found)
            f.IsSelected = ReferenceEquals(f, row);
        Host = row.Address;
    }

    private bool CanConnect() => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var host = Host.Trim();
        if (host.Length == 0)
        {
            ConnectError = "보드 주소를 넣어 주세요";
            ConnectHelp = "";
            return;
        }
        var target = new BoardTarget(host, string.IsNullOrWhiteSpace(User) ? "root" : User.Trim());
        IsConnecting = true;
        ConnectError = ConnectHelp = "";
        CanForgetHostKey = false;
        try
        {
            if (IsPasswordMode)
            {
                if (Password.Length == 0)
                {
                    ConnectError = "보드의 비밀번호를 넣어 주세요";
                    return;
                }
                ConnectProgress = "이 PC의 키를 보드에 넣는 중…";
                var askpass = Environment.ProcessPath ?? throw new BoardException(BoardFailure.Command, "앱 파일 위치를 알 수 없어요");
                await BoardKeys.RegisterAsync(target, Password, askpass);
                Password = "";
                IsKeyMode = true;
            }
            ConnectProgress = "연결하는 중…";
            await Board.ConnectAsync(target);
            _shell.PostLog($"보드 {target.Address} 연결 · 실행기 {Board.Agent?.RunnerVersion}");
            IsStatusTab = true;
            Run.OnShown();
        }
        catch (BoardException ex)
        {
            ShowFailure(ex);
        }
        finally
        {
            IsConnecting = false;
            ConnectProgress = "";
        }
    }

    private void ShowFailure(BoardException ex)
    {
        ConnectError = ex.Message;
        ConnectHelp = ex.Failure switch
        {
            BoardFailure.Unreachable => "보드가 켜져 있고 PC와 같은 공유기에 연결돼 있는지 확인해 주세요.",
            BoardFailure.KeyRefused => IsPasswordMode ? "보드의 비밀번호를 다시 확인해 주세요." : "처음이면 ‘처음 연결 — 비밀번호 입력’을 골라 주세요.",
            BoardFailure.HostKeyChanged => "보드를 새로 설치했다면 이 PC가 기억하는 예전 키를 지우고 다시 연결하세요.",
            BoardFailure.NoSsh => "Windows 설정 › 시스템 › 선택적 기능에서 ‘OpenSSH 클라이언트’를 추가해 주세요.",
            _ => "",
        };
        CanForgetHostKey = ex.Failure == BoardFailure.HostKeyChanged;
        if (ex.Detail is { Length: > 0 } detail)
            _shell.PostLog("보드: " + detail.Trim());
    }

    /// <summary>"예전 키 지우기": forgets the board's old key in the PC's known_hosts (ssh-keygen -R), then connects again.</summary>
    [RelayCommand]
    private async Task ForgetHostKeyAsync()
    {
        if (SshClient.FindSsh() is not { } ssh)
            return;
        var keygen = Path.Combine(Path.GetDirectoryName(ssh)!, "ssh-keygen.exe");
        var psi = new ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add(Host.Trim());
        using (var process = Process.Start(psi)!)
        {
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        Run.StopCommand.Execute(null);
        Manage.StopSoakCommand.Execute(null);
        await Board.DisconnectAsync();
    }

    partial void OnIsKeyModeChanged(bool value)
    {
        if (value) IsPasswordMode = false;
    }

    partial void OnIsPasswordModeChanged(bool value)
    {
        if (value) IsKeyMode = false;
    }

    // ---------------------------------------------------------------- tabs
    [ObservableProperty] private bool _isStatusTab = true;
    [ObservableProperty] private bool _isRunTab;
    [ObservableProperty] private bool _isManageTab;

    partial void OnIsStatusTabChanged(bool value)
    {
        if (!value) return;
        IsRunTab = IsManageTab = false;
    }

    partial void OnIsRunTabChanged(bool value)
    {
        if (!value) return;
        IsStatusTab = IsManageTab = false;
        Run.OnShown();
    }

    partial void OnIsManageTabChanged(bool value)
    {
        if (!value) return;
        IsStatusTab = IsRunTab = false;
        _ = Manage.RefreshAsync();
    }

    public void ShowRunTab()
    {
        IsStatusTab = IsManageTab = false;
        IsRunTab = true;
    }

    // ---------------------------------------------------------------- header
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _chipText = "연결 안 됨";
    [ObservableProperty] private ChipKind _chipKind = ChipKind.Muted;
    [ObservableProperty] private string _headerCaption = "";
    [ObservableProperty] private string _lostText = "";

    // ---------------------------------------------------------------- status
    [ObservableProperty] private string _cpuValue = "0";
    [ObservableProperty] private double _cpuFraction;
    [ObservableProperty] private string _npuValue = "0";
    [ObservableProperty] private double _npuFraction;
    [ObservableProperty] private string _memoryValue = "0";
    [ObservableProperty] private double _memoryFraction;
    [ObservableProperty] private string _temperatureValue = "0";
    [ObservableProperty] private double _temperatureFraction;
    [ObservableProperty] private IReadOnlyList<MeterRow> _cores = [];
    [ObservableProperty] private IReadOnlyList<MeterRow> _npus = [];
    [ObservableProperty] private IReadOnlyList<MeterRow> _memory = [];
    [ObservableProperty] private IReadOnlyList<MeterRow> _temperatures = [];
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _osText = "";
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string _networkText = "";

    private void OnBoardChanged()
    {
        IsConnected = Board.IsConnected;
        LostText = Board.IsConnected ? "" : Board.LostText;
        ChipText = !Board.IsConnected ? "연결 안 됨" : Board.IsRunning ? "실행 중" : "연결됨";
        ChipKind = !Board.IsConnected ? ChipKind.Muted : Board.IsRunning ? ChipKind.Running : ChipKind.Ok;
        HeaderCaption = Board.IsConnected && Board.Target is { } t ? $"{Board.Name} · {t.Host}" : "";
        if (Board.Status is { } s && Board.IsConnected)
            ShowStatus(s);
        Run.OnBoardChanged();
    }

    private void ShowStatus(BoardStatus s)
    {
        CpuValue = Num(s.CpuAll);
        CpuFraction = s.CpuAll / 100;
        NpuValue = Num(s.NpuBusy);
        NpuFraction = s.NpuBusy / 100;
        MemoryValue = Num(s.MemoryUsedPercent);
        MemoryFraction = s.MemoryUsedPercent / 100;
        TemperatureValue = Num(s.MaxTemperature);
        TemperatureFraction = Math.Clamp(s.MaxTemperature / 100, 0, 1);
        Cores = s.Cpu.Select((c, i) => new MeterRow($"코어 {i + 1}", c / 100, $"{Num(c)}%")).ToList();
        Npus = s.Npus.Select((n, i) => n.Busy > 0
            ? new MeterRow($"NPU {i}", n.Busy / 100, $"{Num(n.Busy)}%")
            : new MeterRow($"NPU {i}", 0, "쉬는 중", Muted: true)).ToList();
        Memory =
        [
            new MeterRow("보드", s.MemoryUsedPercent / 100, BoardAgent.Mb(s.Memory.Available), " 남음"),
            new MeterRow("NPU용", s.NpuMemory.UsedPercent / 100, BoardAgent.Mb(s.NpuMemory.Free), " 남음"),
        ];
        var sensor = 0;
        Temperatures = s.Temperatures.Select(t => new MeterRow(
            t.Name.StartsWith("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU" : $"센서 {++sensor}",
            Math.Clamp(t.Celsius / 100, 0, 1), t.Celsius.ToString("0.0", CultureInfo.InvariantCulture) + "°C")).ToList();
        NameText = s.Host;
        OsText = s.OsShort;
        DiskText = $"{BoardAgent.Mb(s.Disk.Free)} 남음";
        UptimeText = Uptime(s.UptimeSeconds);
        NetworkText = s.Network.Mbps > 0 ? $"{s.Network.Mbps} Mbps" : "—";
    }

    private static string Num(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"2일 5시간", "5시간 12분", "12분".</summary>
    public static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}일 {t.Hours}시간"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분"
            : $"{Math.Max(1, t.Minutes)}분";
    }
}
