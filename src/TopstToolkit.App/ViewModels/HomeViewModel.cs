using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.App.ViewModels;

/// <summary>One line of the recent-projects list.</summary>
public sealed class ProjectRow
{
    public ProjectRow(ProjectFile file, DateTimeOffset now)
    {
        File = file;
        Name = file.Name;
        ModelFile = file.Model is not null ? ShellViewModel.ModelTitle(file) : "모델 없음";
        Kind = PresetCatalog.DescribeKind(file.Task, file.Preset);
        (StatusText, StatusKind) = file.Headline switch
        {
            { IsBuilt: true, NeedsRebuild: true } => ("다시 필요", ChipKind.Warn),
            { IsBuilt: true } => ("빌드 완료", ChipKind.Ok),
            _ => ("준비 중", ChipKind.Muted),
        };
        PerfText = file.Headline.InferenceTimeMs is { } ms ? ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "—";
        UpdatedText = Friendly(file.UpdatedAt, now);
    }

    public ProjectFile File { get; }
    public string Name { get; }
    public string ModelFile { get; }
    public string Kind { get; }
    public string StatusText { get; }
    public ChipKind StatusKind { get; }
    public string PerfText { get; }
    public string UpdatedText { get; }

    /// <summary>"오늘 23:07", "어제 18:22", "11월 18일", "2025년 3월 2일".</summary>
    public static string Friendly(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return "오늘 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today.AddDays(-1)) return "어제 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Year == today.Year ? $"{local.Month}월 {local.Day}일" : $"{local.Year}년 {local.Month}월 {local.Day}일";
    }
}

public enum EngineState
{
    Checking,
    Ready,
    Problem,
    Missing,
}

/// <summary>The engine card: is the bundled toolkit usable on this PC.</summary>
public partial class EngineStatusViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusKind))]
    private EngineState _state = EngineState.Checking;

    [ObservableProperty] private string _sdk = "확인 중";
    [ObservableProperty] private string _boardTools = "";
    [ObservableProperty] private string _workspace = "";
    [ObservableProperty] private string _problem = "";

    public string StatusText => State switch
    {
        EngineState.Ready => "준비됨",
        EngineState.Checking => "확인 중",
        EngineState.Missing => "엔진 없음",
        _ => "문제 있음",
    };

    public ChipKind StatusKind => State switch
    {
        EngineState.Ready => ChipKind.Ok,
        EngineState.Checking => ChipKind.Running,
        _ => ChipKind.Bad,
    };
}

/// <summary>Home: recent projects, new project, engine state. Layout-independent.</summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;

    public HomeViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        Engine.Workspace = services.Workspace.Root;
        if (TopstToolkit.Core.Zoo.ZooCatalog.Locate() is { } zoo)
            ZooCounts = $"이미지 분류 {zoo.Models.Count(m => m.Task == "class")} · 객체 검출 {zoo.Models.Count(m => m.Task == "obj")}";
        RefreshEngineFacts();
        Refresh();
    }

    /// <summary>"이미지 분류 142 · 객체 검출 12" on the model zoo card; empty when the app has no zoo catalog.</summary>
    public string ZooCounts { get; } = "";
    public bool HasZoo => ZooCounts.Length > 0;

    /// <summary>"모델 모음 열기" (design §5.9): a start without a model file.</summary>
    [RelayCommand]
    private void OpenZoo() => _shell.NavigateTo("zoo");

    /// <summary>After components were installed or removed.</summary>
    public void RefreshEngineFacts() => Engine.BoardTools = _services.BoardToolsInstalled ? "설치됨" : "설치 안 됨";

    public ObservableCollection<ProjectRow> Recent { get; } = [];
    public EngineStatusViewModel Engine { get; } = new();

    [ObservableProperty] private bool _hasProjects;

    /// <summary>One line telling why something the user asked for did not happen (for example while a build runs).</summary>
    [ObservableProperty] private string _notice = "";

    public void Refresh()
    {
        Notice = "";
        Recent.Clear();
        var now = DateTimeOffset.Now;
        foreach (var project in _services.Projects.List())
            Recent.Add(new ProjectRow(project, now));
        HasProjects = Recent.Count > 0;
    }

    /// <summary>Runs the engine self-check once; the app starts usable while this is still running.</summary>
    public async Task CheckEngineAsync()
    {
        if (_services.Engine is null)
        {
            Engine.State = EngineState.Missing;
            Engine.Sdk = "—";
            Engine.Problem = "엔진이 아직 설치되지 않았어요. 홈에서 설치할 수 있어요.";
            _shell.RefreshIdleStatus(); // "엔진 없음" on the left of the task bar
            return;
        }
        try
        {
            var report = await _services.CheckEngineAsync();
            if (report is { Ok: true })
            {
                Engine.State = EngineState.Ready;
                Engine.Sdk = "ENLIGHT " + report.SdkVersion;
                Engine.Problem = "";
                _shell.EngineCaption = "엔진 SDK " + report.SdkVersion;
            }
            else
            {
                Engine.State = EngineState.Problem;
                Engine.Sdk = report?.SdkVersion is { } v ? "ENLIGHT " + v : "—";
                Engine.Problem = report?.Problems.FirstOrDefault() ?? "엔진 점검이 끝나지 않았어요.";
                _shell.EngineCaption = "엔진 문제";
            }
        }
        catch (Exception ex)
        {
            Engine.State = EngineState.Problem;
            Engine.Problem = ex.Message;
            _shell.EngineCaption = "엔진 문제";
        }
    }

    private const string BusyNotice = "지금 하는 작업이 끝난 뒤에 다른 프로젝트를 열 수 있어요.";

    /// <summary>Opens an empty project; it is saved once a model is brought in.</summary>
    [RelayCommand]
    private void NewProject()
    {
        if (!_shell.StartNewProject())
            Notice = BusyNotice;
    }

    /// <summary>"파일 고르기": a new project that starts with the chosen model.</summary>
    [RelayCommand]
    private void PickModel()
    {
        if (_shell.IsBusy)
        {
            Notice = BusyNotice;
            return;
        }
        if (_services.Dialogs.PickModelFile() is { } path)
            ImportDropped(path);
    }

    /// <summary>A model file dropped on the home screen becomes a new project.</summary>
    public void ImportDropped(string path)
    {
        if (!_shell.StartNewProject(path))
            Notice = BusyNotice;
    }

    [RelayCommand]
    private void QuickCheck() => _shell.NavigateTo("check");

    [RelayCommand]
    private void Open(ProjectRow? row)
    {
        if (row is null)
            return;
        if (ReferenceEquals(_shell.Project, row.File) || _shell.Project?.Id == row.File.Id)
        {
            // already open (maybe building): go back to it
            _shell.NavigateTo(_shell.IsBuilding ? "build" : "model");
            return;
        }
        if (!_shell.OpenProject(row.File))
            Notice = BusyNotice;
    }

    /// <summary>Deletes a project folder after asking. The open project cannot be deleted while it works.</summary>
    [RelayCommand]
    private void Delete(ProjectRow? row)
    {
        if (row is null)
            return;
        var isOpen = _shell.Project?.Id == row.File.Id;
        if (isOpen && _shell.IsBusy)
        {
            Notice = "작업 중인 프로젝트는 지울 수 없어요.";
            return;
        }
        if (!_services.Dialogs.Confirm("프로젝트 삭제", $"\"{row.Name}\" 프로젝트와 그 안의 빌드 결과를 모두 지웁니다. 되돌릴 수 없어요. 지울까요?"))
            return;
        if (isOpen)
            _shell.CloseProject();
        try
        {
            _services.Projects.Delete(row.File.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Notice = "지우지 못했어요. 폴더 안의 파일을 다른 프로그램이 쓰고 있는지 확인해 주세요.";
        }
        Refresh();
    }
}
