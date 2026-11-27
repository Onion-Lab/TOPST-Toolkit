using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Settings;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.App.ViewModels;

/// <summary>One of the three screen layouts (decision D9). Only the ones this build has can be chosen.</summary>
public sealed record LayoutChoice(string Id, string Letter, string Name, string Description, bool IsAvailable, bool IsSelected)
{
    public string ChipText => IsSelected ? "사용 중" : IsAvailable ? "" : "다음 단계에서 추가";
    public ChipKind ChipKind => IsSelected ? ChipKind.Ok : ChipKind.Muted;
}

/// <summary>App page "설정": where the work is kept, the screen layout, the engine and what this app is. Layout-independent.</summary>
public partial class SettingsViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;

    public SettingsViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Update))
                OnPropertyChanged(nameof(Update));
        };
    }

    public ObservableCollection<LayoutChoice> Layouts { get; } = [];
    public ObservableCollection<FactRow> Engine { get; } = [];
    public ObservableCollection<FactRow> About { get; } = [];

    [ObservableProperty] private string _workspaceText = "";
    [ObservableProperty] private string _workspaceNote = "";
    [ObservableProperty] private bool _workspaceNoteIsWarning;
    /// <summary>내 문서\TOPST Toolkit, or the folder the user chose (SaveFolders): exports and board results.</summary>
    [ObservableProperty] private string _saveRootText = "";
    [ObservableProperty] private string _saveRootNote = "";
    [ObservableProperty] private string _logDaysText = "";
    [ObservableProperty] private string _engineChipText = "";
    [ObservableProperty] private ChipKind _engineChipKind;
    [ObservableProperty] private bool _isChecking;

    // ---- the tabs (docs/mockups/mockup-components.html ⑤)
    [ObservableProperty] private bool _isWorkspaceTab = true;
    [ObservableProperty] private bool _isComponentsTab;
    [ObservableProperty] private bool _isLayoutTab;
    [ObservableProperty] private bool _isAboutTab;

    /// <summary>What the app installed from the official sources (design D12), the same list the components page uses.</summary>
    public ComponentsViewModel Components => _shell.Components;

    /// <summary>Opens 설정 on the components tab (from a "설치" link elsewhere).</summary>
    public void ShowComponentsTab()
    {
        IsWorkspaceTab = IsLayoutTab = IsAboutTab = false;
        IsComponentsTab = true;
    }

    /// <summary>App updates (design §7.5): the card at the top of 정보.</summary>
    public UpdateViewModel Update => _shell.Update;

    /// <summary>Opens 설정 on 정보, where the update card is (the header's update pill).</summary>
    public void ShowAboutTab()
    {
        IsWorkspaceTab = IsComponentsTab = IsLayoutTab = false;
        IsAboutTab = true;
    }

    public void OnShown()
    {
        Components.RefreshStates();
        WorkspaceText = _services.Workspace.Root;
        if (_services.Settings.WorkspaceRoot is { } configured && !string.Equals(configured, _services.Workspace.Root, StringComparison.OrdinalIgnoreCase))
            ShowWorkspaceNote($"다음에 프로그램을 켜면 {configured} 을(를) 씁니다.", warning: false);
        LogDaysText = _services.Settings.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
        SaveRootText = SaveFolders.Root(_services.Settings.SaveRoot);

        Layouts.Clear();
        var current = _services.Settings.EffectiveLayout;
        (string Id, string Letter, string Name, string Description)[] all =
        [
            (AppSettings.LayoutRail, "A", "단계 레일", "왼쪽 단계 목록을 따라 한 화면에 한 단계씩"),
            (AppSettings.LayoutPipeline, "B", "파이프라인 보드", "모든 단계를 한 줄로 놓고 한눈에"),
            (AppSettings.LayoutFocus, "C", "포커스", "지금 할 일 하나만 크게"),
        ];
        foreach (var (id, letter, name, description) in all)
            Layouts.Add(new LayoutChoice(id, letter, name, description, AppSettings.AvailableLayouts.Contains(id), id == current));

        ShowEngine();
        About.Clear();
        About.Add(new FactRow("TOPST Toolkit", AppServices.AppVersion));
        About.Add(new FactRow("ENLIGHT SDK", AppServices.SdkVersion));
        About.Add(new FactRow("대상 보드", "TOPST AI-G (TCC750x NPU)"));
    }

    private void ShowEngine()
    {
        Engine.Clear();
        var layout = _services.EngineLayout;
        var report = _services.LastEngineCheck;
        (EngineChipText, EngineChipKind) = _services.Engine is null ? ("엔진 없음", ChipKind.Bad)
            : report is null ? ("확인 전", ChipKind.Muted)
            : report.Ok ? ("준비됨", ChipKind.Ok) : ("문제 있음", ChipKind.Bad);
        Engine.Add(new FactRow("SDK", report?.SdkVersion is { } v ? "ENLIGHT " + v : "—"));
        Engine.Add(new FactRow("Python", report?.Python ?? "—"));
        if (report?.Packages?.GetValueOrDefault("torch") is { } torch)
            Engine.Add(new FactRow("PyTorch", torch));
        Engine.Add(new FactRow("보드용 빌드 도구", _services.BoardToolsInstalled ? "설치됨 (Arm GNU 9.2)" : "설치 안 됨"));
        Engine.Add(new FactRow("엔진 위치", layout?.RuntimeRoot ?? "찾지 못함", Mono: true));
        foreach (var problem in report?.Problems ?? [])
            Engine.Add(new FactRow("문제", problem));
    }

    [RelayCommand]
    private async Task RecheckAsync()
    {
        if (IsChecking)
            return;
        IsChecking = true;
        EngineChipText = "확인 중";
        EngineChipKind = ChipKind.Running;
        await _shell.Home.CheckEngineAsync();
        IsChecking = false;
        ShowEngine();
    }

    /// <summary>The workspace must be an ASCII path (the toolkit cannot read Korean paths); it applies from the next start.</summary>
    [RelayCommand]
    private void ChangeWorkspace()
    {
        if (_services.Dialogs.PickFolder("작업 공간 폴더 고르기", _services.Workspace.Root) is not { } folder)
            return;
        if (NameRules.WorkspaceRootProblem(folder) is { } problem)
        {
            ShowWorkspaceNote(problem, warning: true);
            return;
        }
        _services.Settings.WorkspaceRoot = folder;
        if (Save())
            ShowWorkspaceNote($"다음에 프로그램을 켜면 {folder} 을(를) 씁니다. 지금 있는 프로젝트는 옮겨지지 않아요.", warning: false);
    }

    [RelayCommand]
    private void OpenWorkspace() => _services.Dialogs.ShowInExplorer(_services.Workspace.Root);

    /// <summary>
    /// The folder for what the user keeps. Any folder works, Korean names and OneDrive included: only the app writes
    /// there, never the toolkit. Applies at once; what was saved before stays where it is.
    /// </summary>
    [RelayCommand]
    private void ChangeSaveRoot()
    {
        if (_services.Dialogs.PickFolder("저장 폴더 고르기", SaveFolders.Prepared(SaveRootText)) is not { } folder)
            return;
        var standard = SaveFolders.Root();
        _services.Settings.SaveRoot = string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(standard), StringComparison.OrdinalIgnoreCase) ? null : folder;
        if (!Save())
            return;
        SaveRootText = SaveFolders.Root(_services.Settings.SaveRoot);
        SaveRootNote = SaveFolders.EnsureCreated(_services.Settings.SaveRoot)
            ? $"이제부터 {SaveFolders.ExportsName} · {SaveFolders.BoardResultsName} 폴더를 여기에 만들어 씁니다. 전에 저장한 파일은 그대로 있어요."
            : "이 폴더에 쓸 수 없어요. 다른 폴더를 골라 주세요.";
    }

    [RelayCommand]
    private void OpenSaveRoot()
    {
        SaveFolders.EnsureCreated(_services.Settings.SaveRoot);
        _services.Dialogs.ShowInExplorer(SaveRootText);
    }

    partial void OnLogDaysTextChanged(string value)
    {
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650
            && days != _services.Settings.LogRetentionDays)
        {
            _services.Settings.LogRetentionDays = days;
            Save();
        }
    }

    private void ShowWorkspaceNote(string text, bool warning)
    {
        WorkspaceNote = text;
        WorkspaceNoteIsWarning = warning;
    }

    private bool Save()
    {
        try
        {
            _services.Settings.Save();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowWorkspaceNote("설정을 저장하지 못했어요. " + ex.Message, warning: true);
            return false;
        }
    }
}
