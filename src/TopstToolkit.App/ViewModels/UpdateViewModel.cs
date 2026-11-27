using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Text;
using TopstToolkit.Core.Updates;

namespace TopstToolkit.App.ViewModels;

/// <summary>Where an app update stands (docs/mockups/mockup-update.html).</summary>
public enum UpdateStage
{
    /// <summary>Not checked in this session (the check at start is off, or has not run yet).</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer version is out: ③ "업데이트 받기".</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and checked: ⑤ "다시 켜서 설치".</summary>
    Ready,
    /// <summary>Setup was started; the app closes as soon as Setup says it runs.</summary>
    Starting,
    /// <summary>The first start after an update that went through: ⑥.</summary>
    Updated,
    Failed,
}

/// <summary>The header pill's colour: a new version (tint), done (green), or not done (red).</summary>
public enum UpdatePillKind
{
    New,
    Done,
    Failed,
}

/// <summary>
/// App updates on screen (design §7.5, docs/mockups/mockup-update.html): checks the update address when the app starts,
/// shows a pill in the header when there is a newer version, and drives the card at the top of 설정 › 정보 — what
/// changed, the download, the hand-over to Setup that closes the app and starts it again, and how the update ended.
/// Layout-independent (D9): every layout shows the same pill and card.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    /// <summary>How long after start the check waits, so it does not compete with the start itself.</summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(4);

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly UpdateChecker? _checker;
    private readonly SetupRecord? _setup;
    private readonly Action _closeApp;
    private readonly Version _current;
    private CancellationTokenSource? _download;
    private UpdateManifest? _manifest;
    private string? _installer;
    private UpdateOutcome? _outcome;
    private UpdateProblem? _problem;
    private bool _installFailed;
    private readonly List<(long Ticks, long Bytes)> _samples = [];

    /// <param name="source">Where to look; null (a build without an update address) hides the update screens.</param>
    /// <param name="setup">What Setup recorded about this copy; null when Setup did not install it (no install from the app).</param>
    /// <param name="closeApp">Closes the app once Setup runs (captures replace it).</param>
    public UpdateViewModel(AppServices services, ShellViewModel shell, UpdateSource? source, SetupRecord? setup,
        UpdateChecker? checker = null, Action? closeApp = null)
    {
        _services = services;
        _shell = shell;
        _setup = setup;
        _checker = checker ?? (source is null ? null : new UpdateChecker(source));
        _closeApp = closeApp ?? (() => Application.Current?.Shutdown());
        _current = UpdateVersion.Parse(AppServices.AppVersion);
        _checkOnStart = services.Settings.UpdateCheck;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsBusy))
                Refresh();
        };
        Refresh();
    }

    /// <summary>The downloads of this feature: the installer, the note for the next start, Setup's log.</summary>
    public string Folder => Path.Combine(_services.Workspace.DownloadsDir, "updates");

    [ObservableProperty] private UpdateStage _stage = UpdateStage.Idle;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    /// <summary>The card in 설정 › 정보 is there: this build has an update address, or an update just ended.</summary>
    public bool IsAvailable => _checker is not null || _outcome is not null || _forCapture;

    private bool _forCapture;

    public string CurrentText => UpdateVersion.Show(_current);
    private string NewText => _manifest is null ? (_outcome?.Pending.To ?? "") : UpdateVersion.Show(_manifest.ParsedVersion);

    // ---- the header pill
    public bool ShowsPill => PillTextFor() is not null;
    public string PillText => PillTextFor() ?? "";
    public UpdatePillKind PillKind => Stage == UpdateStage.Updated ? UpdatePillKind.Done : _installFailed ? UpdatePillKind.Failed : UpdatePillKind.New;
    public bool PillIsDone => PillKind == UpdatePillKind.Done;
    public bool PillIsFailed => PillKind == UpdatePillKind.Failed;
    public bool PillIsNew => PillKind == UpdatePillKind.New;

    private string? PillTextFor() => Stage switch
    {
        UpdateStage.Available => $"새 버전 {NewText}",
        UpdateStage.Downloading => $"업데이트 받는 중 {Progress * 100:0}%",
        UpdateStage.Ready or UpdateStage.Starting => "업데이트 준비됨",
        UpdateStage.Updated => $"{Korean.VersionToward(NewText)} 업데이트했어요",
        UpdateStage.Failed when _installFailed => "업데이트 안 됨",
        UpdateStage.Failed when _manifest is not null => $"새 버전 {NewText}",
        _ => null,
    };

    // ---- the card
    /// <summary>The card stands out (tint ring) while there is something to do.</summary>
    public bool IsHighlighted => Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready or UpdateStage.Starting;

    public string ChipText => Stage switch
    {
        UpdateStage.Idle => "확인 전",
        UpdateStage.Checking => "확인 중",
        UpdateStage.UpToDate => "최신",
        UpdateStage.Available => "새 버전",
        UpdateStage.Downloading => "받는 중",
        UpdateStage.Ready => "받음",
        UpdateStage.Starting => "설치 시작",
        UpdateStage.Updated => "업데이트됨",
        _ => _installFailed ? "업데이트 안 됨" : _problem switch
        {
            UpdateProblem.Offline => "확인 못 함",
            UpdateProblem.Interrupted => "멈춤",
            UpdateProblem.BadSignature or UpdateProblem.BadManifest => "확인 못 함",
            _ => "설치 안 함",
        },
    };

    public ChipKind ChipKind => Stage switch
    {
        UpdateStage.Idle => ChipKind.Muted,
        UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Starting => ChipKind.Running,
        UpdateStage.UpToDate or UpdateStage.Ready or UpdateStage.Updated => ChipKind.Ok,
        UpdateStage.Available => ChipKind.Info,
        _ => _problem is UpdateProblem.Offline or UpdateProblem.Interrupted or UpdateProblem.SetupNotStarted && !_installFailed ? ChipKind.Warn : ChipKind.Bad,
    };

    public string Headline => Stage switch
    {
        UpdateStage.Available => $"TOPST Toolkit {NewText}",
        UpdateStage.Downloading => $"TOPST Toolkit {NewText} 받는 중",
        UpdateStage.Ready => $"{Korean.VersionObject(NewText)} 설치할 준비가 됐어요",
        UpdateStage.Starting => "설치를 시작하는 중",
        UpdateStage.Updated => $"{Korean.VersionToward(NewText)} 업데이트했어요",
        UpdateStage.Failed when _installFailed => "업데이트를 마치지 못했어요",
        UpdateStage.Failed when _problem is UpdateProblem.Mismatch => "업데이트를 설치하지 않았어요",
        UpdateStage.Failed when _problem is UpdateProblem.SetupNotStarted => "설치를 시작하지 못했어요",
        UpdateStage.Failed when _problem is UpdateProblem.Interrupted => $"TOPST Toolkit {NewText}",
        _ => $"TOPST Toolkit {CurrentText}",
    };

    public string Lead => Stage switch
    {
        UpdateStage.Idle => _problem switch
        {
            UpdateProblem.Offline => "새 버전을 확인하지 못했어요. 인터넷 연결을 확인해 주세요.",
            not null => "새 버전을 확인하지 못했어요.",
            _ => "새 버전이 있는지 아직 확인하지 않았어요.",
        },
        UpdateStage.Checking => "새 버전이 있는지 확인하는 중이에요.",
        UpdateStage.UpToDate => "최신 버전이에요.",
        UpdateStage.Available => string.Join(" · ", new[] { $"지금 쓰는 버전 {CurrentText}", ReleasedText(), SizeText() }.Where(s => s.Length > 0)),
        UpdateStage.Downloading => "받는 동안 다른 작업을 해도 돼요.",
        UpdateStage.Ready => "받은 파일이 올라온 파일과 같은지 확인했어요.",
        UpdateStage.Starting => _setup?.AllUsers == false ? "곧 앱이 닫히고 설치 창이 보여요." : "Windows가 물으면 [예]를 눌러 주세요. 곧 앱이 닫히고 설치 창이 보여요.",
        UpdateStage.Updated => UpdatedText(),
        _ => _installFailed ? $"{Korean.VersionObject(CurrentText)} 그대로 쓰고 있어요." : _problem switch
        {
            UpdateProblem.Offline => "새 버전을 확인하지 못했어요. 인터넷 연결을 확인해 주세요.",
            UpdateProblem.BadSignature or UpdateProblem.BadManifest => "올라온 새 버전 정보가 TOPST Toolkit에서 보낸 것인지 확인되지 않아서 쓰지 않았어요.",
            UpdateProblem.Interrupted => "받다가 멈췄어요. 받은 데까지는 남아 있어요.",
            UpdateProblem.Mismatch => "받은 파일이 올라온 파일과 달라요. 받다가 깨졌거나 누가 바꿔 놓았을 수 있어서, 쓰지 않고 지웠어요.",
            UpdateProblem.SetupNotStarted => "권한을 받지 못해 설치하지 않았어요. 지금 버전을 그대로 써요.",
            _ => "",
        },
    };

    public ObservableCollection<string> Notes { get; } = [];
    public string NotesTitle => Stage == UpdateStage.Updated ? "이번에 바뀐 점" : "바뀐 점";
    public bool ShowsNotes => Notes.Count > 0 && Stage is UpdateStage.Available or UpdateStage.Updated;
    public bool IsDownloading => Stage == UpdateStage.Downloading;

    // ---- the one action of the card
    public string PrimaryText => Stage switch
    {
        UpdateStage.Available when _setup is null => "받는 곳 열기",
        UpdateStage.Available => "업데이트 받기",
        UpdateStage.Ready => "다시 켜서 설치",
        UpdateStage.Failed when _installFailed => "다시 시도",
        UpdateStage.Failed => _problem switch
        {
            UpdateProblem.Interrupted => "이어서 받기",
            UpdateProblem.Mismatch => "다시 받기",
            UpdateProblem.SetupNotStarted => "다시 시도",
            _ => "다시 확인",
        },
        _ => "",
    };

    public bool ShowsPrimary => PrimaryText.Length > 0 && !(Stage == UpdateStage.Available && _setup is null && _manifest?.Page is null);

    /// <summary>The filled button for the step forward; the outlined one for a retry or a page to open.</summary>
    public bool PrimaryIsAccent => Stage is UpdateStage.Available or UpdateStage.Ready && !(Stage == UpdateStage.Available && _setup is null);

    public string PrimaryHelp => Stage switch
    {
        UpdateStage.Available when _setup is null => "이 앱은 설치 파일로 설치하지 않아서, 새 버전은 직접 받아야 해요.",
        UpdateStage.Available => "설치할 때 앱이 잠시 닫혔다가 다시 켜져요. 프로젝트와 설정은 그대로예요.",
        UpdateStage.Ready when _shell.IsBusy => "하던 작업이 끝나면 설치할 수 있어요.",
        UpdateStage.Ready => "앱이 닫히고 설치 창이 1분쯤 보였다가 다시 켜져요.",
        UpdateStage.Failed when _problem is UpdateProblem.Mismatch => "다시 받아도 같으면 홈페이지에서 직접 받아 주세요.",
        _ => "",
    };

    /// <summary>Setup in Program Files asks Windows for administrator rights: say so before the button is pressed.</summary>
    public bool ShowsAdminNote => Stage is UpdateStage.Ready or UpdateStage.Starting && _setup is not null && _setup.AllUsers != false;

    public bool ShowsLog => Stage == UpdateStage.Failed && _installFailed && _outcome?.Pending.Log is { } log && File.Exists(log);

    // ---- the foot of the card
    private bool _checkOnStart;

    /// <summary>"켤 때 새 버전 확인" (설정, default on).</summary>
    public bool CheckOnStart
    {
        get => _checkOnStart;
        set
        {
            if (!SetProperty(ref _checkOnStart, value))
                return;
            _services.Settings.UpdateCheck = value;
            SaveSettings();
        }
    }

    public string LastCheckedText => _services.Settings.UpdateCheckedAt is { } at ? $"{When(at)}에 확인" : "";

    // ---------------------------------------------------------------------------------------------------- start

    /// <summary>
    /// At start: how the last update ended (once), then — when the check is on — a quiet look for a newer version.
    /// Problems of that quiet look stay in the card; the pill appears only for a newer version.
    /// </summary>
    public async Task StartAsync()
    {
        _outcome = PendingUpdate.Take(Folder, _current);
        if (_outcome is { } outcome)
        {
            Notes.Clear();
            foreach (var note in outcome.Pending.Notes)
                Notes.Add(note);
            _installFailed = !outcome.Succeeded;
            Stage = outcome.Succeeded ? UpdateStage.Updated : UpdateStage.Failed;
            Refresh();
        }
        TraceState("start");
        if (_checker is null || !_services.Settings.UpdateCheck)
            return;
        await Task.Delay(StartDelay);
        await CheckAsync(quiet: true);
        TraceState("checked");
    }

    /// <summary>Tests: a file where the app writes how it sees the update (build\update\update-test.ps1 — Setup starts the app again).</summary>
    public const string TraceVariable = "TOPST_UPDATE_TRACE";

    private void TraceState(string when)
    {
        if (Environment.GetEnvironmentVariable(TraceVariable) is not { Length: > 0 } path)
            return;
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {CurrentText} {when}: stage {Stage} · pill [{PillText}] · {Headline} · {Lead} · notes {Notes.Count}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => CheckAsync(quiet: false);

    private bool CanCheckNow() => (_checker is not null || _forCapture) && Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Starting);

    private async Task CheckAsync(bool quiet)
    {
        if (_checker is null || Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Starting)
            return;
        var before = Stage;
        if (!quiet || before is UpdateStage.Idle or UpdateStage.UpToDate)
            Stage = UpdateStage.Checking;
        Refresh();
        var result = await _checker.CheckAsync(_current);
        _services.Settings.UpdateCheckedAt = DateTimeOffset.Now;
        SaveSettings();
        if (result.Problem is { } problem)
        {
            Trace.WriteLine($"update check: {problem} {result.Detail}");
            _problem = problem;
            // a quiet look that failed says so in the card only, and leaves an update that just ended on screen
            Stage = quiet ? (before is UpdateStage.Updated or UpdateStage.Failed ? before : UpdateStage.Idle) : UpdateStage.Failed;
            Refresh();
            return;
        }
        if (!result.IsNewer)
        {
            if (before is not (UpdateStage.Updated or UpdateStage.Failed) || !quiet)
            {
                _problem = null;
                _installFailed = false;
                Stage = before == UpdateStage.Updated ? UpdateStage.Updated : UpdateStage.UpToDate;
            }
            Refresh();
            return;
        }
        Offer(result.Manifest!);
    }

    private void Offer(UpdateManifest manifest)
    {
        _manifest = manifest;
        _problem = null;
        _installFailed = false;
        Notes.Clear();
        foreach (var note in manifest.Notes)
            Notes.Add(note);
        var path = UpdateChecker.InstallerPath(Folder, manifest);
        if (_setup is not null && UpdateChecker.IsDownloaded(path, manifest))
        {
            _installer = path; // downloaded before (an install that did not go through, or "나중에")
            Stage = UpdateStage.Ready;
        }
        else
        {
            Stage = UpdateStage.Available;
        }
        Refresh();
    }

    // ---------------------------------------------------------------------------------------------------- actions

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task PrimaryAsync()
    {
        switch (Stage)
        {
            case UpdateStage.Available when _setup is null:
                if (_manifest?.Page is { } page)
                    _services.Dialogs.OpenUrl(page);
                break;
            case UpdateStage.Available:
                await DownloadAsync();
                break;
            case UpdateStage.Ready:
                await InstallAsync();
                break;
            case UpdateStage.Failed when _installFailed:
                await CheckAsync(quiet: false);
                break;
            case UpdateStage.Failed when _problem is UpdateProblem.Interrupted or UpdateProblem.Mismatch && _manifest is not null:
                await DownloadAsync();
                break;
            case UpdateStage.Failed when _problem is UpdateProblem.SetupNotStarted && _installer is not null:
                await InstallAsync();
                break;
            case UpdateStage.Failed:
                await CheckAsync(quiet: false);
                break;
        }
    }

    private bool CanPrimary() => Stage switch
    {
        UpdateStage.Ready => !_shell.IsBusy,
        UpdateStage.Failed when _problem is UpdateProblem.SetupNotStarted => !_shell.IsBusy,
        _ => true,
    };

    /// <summary>Downloads the installer, over several connections and resumable; checks it against the signed size and SHA-256.</summary>
    public async Task DownloadAsync()
    {
        if (_checker is null || _manifest is null || Stage == UpdateStage.Downloading)
            return;
        _download = new CancellationTokenSource();
        _samples.Clear();
        Progress = 0;
        ProgressText = "";
        _problem = null;
        Stage = UpdateStage.Downloading;
        Refresh();
        var dispatcher = Application.Current?.Dispatcher;
        long done = 0, total = _manifest.Installer.Size, shown = -1;
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => Show(), dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
        void Show()
        {
            var d = Interlocked.Read(ref done);
            if (d == shown)
                return;
            shown = d;
            Progress = total > 0 ? Math.Clamp((double)d / total, 0, 1) : 0;
            ProgressText = DownloadLine(d, total);
            OnPropertyChanged(nameof(PillText));
        }
        timer.Start();
        try
        {
            _installer = await _checker.DownloadAsync(_manifest, Folder, (d, t) =>
            {
                Interlocked.Exchange(ref done, d);
                if (t > 0)
                    Interlocked.Exchange(ref total, t);
            }, _download.Token);
            Show();
            Stage = UpdateStage.Ready;
        }
        catch (OperationCanceledException)
        {
            Stage = UpdateStage.Available; // what arrived stays for the next try
        }
        catch (UpdateException e)
        {
            Trace.WriteLine($"update download: {e.Problem} {e.Message}");
            _problem = e.Problem;
            Stage = UpdateStage.Failed;
        }
        finally
        {
            timer.Stop();
            _download.Dispose();
            _download = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _download?.Cancel();

    /// <summary>
    /// Leaves a note for the next start, hands over to Setup and closes the app once Setup runs. Setup starts the app
    /// again when it is done, whether the update went through or not.
    /// </summary>
    public async Task InstallAsync()
    {
        if (_manifest is null || _installer is null || _setup is null || _shell.IsBusy || Stage == UpdateStage.Starting)
            return;
        Stage = UpdateStage.Starting;
        Refresh();
        var version = UpdateVersion.Show(_manifest.ParsedVersion);
        var signal = Path.Combine(Folder, "setup-started-" + Guid.NewGuid().ToString("N")[..8]);
        var log = Path.Combine(Folder, $"setup-{version}.log");
        new PendingUpdate
        {
            From = CurrentText, To = version, Notes = _manifest.Notes, Started = DateTimeOffset.Now, Installer = _installer, Log = log,
        }.Save(Folder);
        var app = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TopstToolkit.exe");
        var arguments = SetupHandoff.Arguments(_setup, signal, app, Environment.ProcessId, log,
            Environment.GetEnvironmentVariable(SetupHandoff.ExtraArgsVariable));
        try
        {
            var result = await SetupHandoff.StartAsync(_installer, _manifest, arguments, signal);
            if (result.Started)
            {
                TryDelete(signal);
                _closeApp();
                return;
            }
            Trace.WriteLine($"update: Setup ended with {result.ExitCode} before it started");
            _problem = UpdateProblem.SetupNotStarted;
        }
        catch (UpdateException e)
        {
            Trace.WriteLine($"update install: {e.Problem} {e.Message}");
            _problem = e.Problem;
            if (e.Problem == UpdateProblem.Mismatch)
            {
                TryDelete(_installer);
                _installer = null;
            }
        }
        TryDelete(Path.Combine(Folder, PendingUpdate.FileName));
        TryDelete(signal);
        Stage = UpdateStage.Failed;
        Refresh();
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (_outcome?.Pending.Log is { } log && File.Exists(log))
            _services.Dialogs.OpenWithShell(log);
    }

    // ---------------------------------------------------------------------------------------------------- captures

    /// <summary>Screen captures (Dev\ScreenCapture --update demo): one state with the data of a made-up release.</summary>
    internal void ShowForCapture(UpdateStage stage, UpdateManifest manifest, UpdateProblem? problem = null, double progress = 0.42,
        bool installFailed = false, TimeSpan? took = null)
    {
        _forCapture = true;
        _manifest = manifest;
        _problem = problem;
        _installFailed = installFailed;
        _outcome = stage == UpdateStage.Updated || installFailed
            ? new UpdateOutcome(!installFailed, new PendingUpdate { From = CurrentText, To = manifest.Version, Notes = manifest.Notes, Started = DateTimeOffset.Now - (took ?? TimeSpan.FromSeconds(72)) }, took ?? TimeSpan.FromSeconds(72))
            : null;
        Notes.Clear();
        foreach (var note in manifest.Notes)
            Notes.Add(note);
        Progress = progress;
        ProgressText = stage == UpdateStage.Downloading ? DownloadLine((long)(manifest.Installer.Size * progress), manifest.Installer.Size, bytesPerSecond: 4.2 * 1048576) : "";
        Stage = stage;
        Refresh();
    }

    // ---------------------------------------------------------------------------------------------------- text

    private string ReleasedText() => _manifest?.ReleasedOn is { } day ? $"{day.Month}월 {day.Day}일에 나옴" : "";

    private string SizeText() => _manifest is null ? "" : $"{_manifest.Installer.Size / 1048576.0:0} MB";

    private string UpdatedText()
    {
        if (_outcome is not { } outcome)
            return "";
        var took = outcome.Took < TimeSpan.FromMinutes(30) ? " · " + Duration(outcome.Took) + " 걸림" : "";
        return $"{When(outcome.Pending.Started + outcome.Took)}{took}";
    }

    /// <summary>"23.5 / 56 MB · 4.2 MB/s · 8초 남음".</summary>
    private string DownloadLine(long done, long total, double? bytesPerSecond = null)
    {
        var now = Environment.TickCount64;
        _samples.Add((now, done));
        _samples.RemoveAll(s => now - s.Ticks > 4000);
        var rate = bytesPerSecond ?? (_samples.Count >= 2 && _samples[^1].Ticks > _samples[0].Ticks
            ? (_samples[^1].Bytes - _samples[0].Bytes) * 1000.0 / (_samples[^1].Ticks - _samples[0].Ticks) : 0);
        var parts = new List<string> { $"{done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB" };
        if (rate > 0)
        {
            parts.Add($"{rate / 1048576.0:0.0} MB/s");
            parts.Add(Duration(TimeSpan.FromSeconds(Math.Max(1, (total - done) / rate))) + " 남음");
        }
        return string.Join(" · ", parts);
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 3 ? $"약 {Math.Round(t.TotalMinutes):0}분"
        : t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초"
        : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}초";

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime().DateTime;
        var today = DateTime.Today;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? $"오늘 {time}"
            : local.Date == today.AddDays(-1) ? $"어제 {time}"
            : $"{local.Month}월 {local.Day}일";
    }

    /// <summary>Every derived property at once: the stage, the busy state and the texts move together.</summary>
    private void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(IsAvailable), nameof(ShowsPill), nameof(PillText), nameof(PillKind), nameof(PillIsDone), nameof(PillIsFailed), nameof(PillIsNew),
                     nameof(IsHighlighted), nameof(ChipText), nameof(ChipKind), nameof(Headline), nameof(Lead), nameof(NotesTitle), nameof(ShowsNotes),
                     nameof(IsDownloading), nameof(PrimaryText), nameof(ShowsPrimary), nameof(PrimaryIsAccent), nameof(PrimaryHelp), nameof(ShowsAdminNote),
                     nameof(ShowsLog), nameof(LastCheckedText),
                 })
            OnPropertyChanged(name);
        PrimaryCommand.NotifyCanExecuteChanged();
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    private void SaveSettings()
    {
        try
        {
            _services.Settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("update: settings not saved: " + e.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
