using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Export;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Board;

namespace TopstToolkit.App.ViewModels;

/// <summary>
/// Step 7 "내보내기": writes the folder that goes to the board, with a README, and the diagnostics package
/// (design §5.6). Layout-independent.
/// </summary>
public partial class ExportViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;

    public ExportViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
        // 내 문서\TOPST Toolkit\내보내기 unless the user exported somewhere else before (SaveFolders, 2026-11-25)
        Destination = services.Settings.ExportFolder ?? TopstToolkit.Core.Workspace.SaveFolders.Exports(services.Settings.SaveRoot);
    }

    public ObservableCollection<FactRow> Contents { get; } = [];
    public ObservableCollection<FactRow> ResultFacts { get; } = [];

    [ObservableProperty] private string _destination;
    [ObservableProperty] private bool _includeRuntime = true;
    [ObservableProperty] private bool _includeProject = true;
    [ObservableProperty] private bool _makeZip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string _problem = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isBusy;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _boardFolderName = "";
    [ObservableProperty] private string _boardCommands = "";
    [ObservableProperty] private bool _hasNetSo;

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultPath = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _readmePath = "";
    [ObservableProperty] private string _diagnosticsPath = "";

    public bool CanExport => Problem.Length == 0 && !IsBusy;

    public void OnShown()
    {
        Error = "";
        var problem = _services.Packager.Problem(_project);
        if (problem is null && _project.Headline.NeedsRebuild)
            problem = "설정이 바뀌었어요. 4 빌드를 다시 끝낸 뒤 내보내 주세요.";
        Problem = problem ?? "";

        Contents.Clear();
        var paths = BuildPaths.For(_services.Projects, _project);
        if (paths is null)
            return;
        BoardFolderName = System.IO.Path.GetFileName(paths.CodeDir);
        HasNetSo = System.IO.File.Exists(paths.NetSo);
        BoardCommands = $"scp -r {BoardFolderName}/ \\\n    root@<보드 IP>:/home/root\n\ntcnnapp -n {BoardFolderName}/";
        Contents.Add(new FactRow(BoardFolderName + "\\", "보드로 복사하는 폴더 — NPU 명령, 가중치, 컴파일 결과 전부"));
        // net.so built with the app's fast box maths says so, and brings the script that checks it on a board
        var postProcess = HasNetSo ? ProjectBuilder.BoardPostProcessOf(_project) : "toolkit";
        var fast = FastPostProcess.SourceOfKind(postProcess) is not null;
        var tail = postProcess == TailCode.Kind;
        Contents.Add(new FactRow("  net.so", !HasNetSo ? "없음 — 보드 도구가 없어요 (설정 › 구성 요소에서 설치)"
            : fast || tail ? "보드용 후처리 라이브러리 · " + FastPostProcess.Describe(postProcess) : "보드용 후처리 라이브러리"));
        Contents.Add(new FactRow("  sample\\input.ia.bin", "결과 확인용 사진의 NPU 입력"));
        Contents.Add(new FactRow("README.md", "보드로 복사하고 실행하는 방법, 고정된 값"));
        if (_services.Packager.LabelsOf(_project) is not null)
            Contents.Add(new FactRow("labels.txt", "클래스 이름"));
        if (fast)
            Contents.Add(new FactRow("board-check\\", "보드에서 빠른 박스 계산과 툴킷 코드의 결과 · 시간을 비교하는 스크립트"));
        else if (tail)
            Contents.Add(new FactRow("board-check\\", "보드에서 잘라낸 구간 코드의 결과를 PC 계산과 비교하는 스크립트와 그 입력"));
    }

    [RelayCommand]
    private void PickDestination()
    {
        if (_services.Dialogs.PickFolder("내보낼 위치 고르기", TopstToolkit.Core.Workspace.SaveFolders.Prepared(Destination) ?? Destination) is { } folder)
            Destination = folder;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!CanExport)
            return;
        IsBusy = true;
        Error = "";
        try
        {
            var options = new ExportOptions(Destination, IncludeRuntime, IncludeProject, MakeZip);
            var result = await Task.Run(() => _services.Packager.Export(_project, options));
            Show(result);
            _services.Settings.ExportFolder = Destination;
            try { _services.Settings.Save(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* remembering the folder is a convenience */ }
            _shell.OnProjectChanged();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = "내보내지 못했어요. " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(ExportResult result)
    {
        HasResult = true;
        ResultPath = result.Root;
        ReadmePath = result.ReadmePath;
        ResultSummary = $"파일 {result.FileCount}개 · {(result.TotalBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        ResultFacts.Clear();
        ResultFacts.Add(new FactRow("보드로 복사할 폴더", System.IO.Path.GetFileName(result.BoardFolder)));
        ResultFacts.Add(new FactRow("net.so", result.HasNetSo ? "들어 있음" : "없음 (README에 만드는 방법이 있어요)"));
        ResultFacts.Add(new FactRow("확인용 입력", result.HasSampleInput ? "sample\\input.ia.bin" : "없음"));
        if (result.ZipPath is { } zip)
            ResultFacts.Add(new FactRow("zip 파일", System.IO.Path.GetFileName(zip)));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ResultPath.Length > 0)
            _services.Dialogs.ShowInExplorer(ResultPath);
    }

    [RelayCommand]
    private void OpenReadme()
    {
        if (ReadmePath.Length > 0)
            _services.Dialogs.OpenWithShell(ReadmePath);
    }

    /// <summary>The logs and settings a developer needs to look into a problem. Never the model or the pictures.</summary>
    [RelayCommand]
    private async Task MakeDiagnosticsAsync()
    {
        if (ProjectStore.IsDraft(_project))
            return;
        try
        {
            var destination = Destination;
            DiagnosticsPath = await Task.Run(() => _services.Diagnostics.Create(_project, destination, AppServices.AppVersion));
            _services.Dialogs.ShowInExplorer(DiagnosticsPath);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = "진단 패키지를 만들지 못했어요. " + ex.Message;
        }
    }

    [RelayCommand]
    private void GoToBuild() => _shell.NavigateTo("build");
}
