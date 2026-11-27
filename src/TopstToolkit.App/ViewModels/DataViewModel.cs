using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.App.ViewModels;

/// <summary>A small picture with its file.</summary>
public sealed record Thumb(string Path, string Name, ImageSource? Image);

/// <summary>
/// Step 3 "데이터": the pictures that set the value ranges of the INT8 network (calibration) and the pictures the
/// result screen shows. Files are copied into the project the moment they are chosen. Layout-independent.
/// </summary>
public partial class DataViewModel : ObservableObject, IPageViewModel
{
    private const int MaxThumbs = 18;

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private bool _loading;
    private bool _shownOnce;

    public DataViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
    }

    public ObservableCollection<Thumb> Thumbs { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<Thumb> TestImages { get; } = [];

    [ObservableProperty] private bool _isSamples;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private bool _isNpy;
    [ObservableProperty] private string _folderText = "";
    [ObservableProperty] private bool _needsFolder;
    [ObservableProperty] private string _countText = "100";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private ChipKind _summaryKind = ChipKind.Muted;
    [ObservableProperty] private string _moreText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _testHelp = "";
    [ObservableProperty] private bool _canEditTest = true;

    /// <summary>Completes when the page has finished copying and loading thumbnails; screen captures wait for it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void OnShown() => Ready = ShowAsync();

    private async Task ShowAsync()
    {
        _loading = true;
        var s = _project.Options.Calibration;
        IsSamples = s.Source == "samples";
        IsFolder = s.Source == "folder";
        IsNpy = s.Source == "npy";
        CountText = s.Count.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        UpdateFolderText();
        Error = "";

        var staged = _services.Data.Describe(_project);
        var first = !_shownOnce;
        _shownOnce = true;
        if (staged.Count == 0 && !NeedsFolder && first)
            await RestageAsync(userChange: false); // first visit: copy the default pictures so there is something to look at
        else
            await Show(staged);
        await LoadTestImagesAsync(ensureSample: true);
    }

    private void UpdateFolderText()
    {
        var s = _project.Options.Calibration;
        NeedsFolder = s.Source != "samples" && string.IsNullOrEmpty(s.SourcePath);
        FolderText = s.Source == "samples" ? "프로그램에 들어 있는 사진 100장" : s.SourcePath ?? "아직 고르지 않았어요";
        CanEditTest = s.Source != "npy";
        TestHelp = s.Source == "npy"
            ? "NPY 데이터는 첫 번째 파일로 결과를 확인합니다."
            : $"빌드할 때마다 이 사진으로 추론해 5 결과에 보여 줍니다. 최대 {DataPreparer.MaxTestImages}장.";
    }

    // ---- where the calibration data comes from
    partial void OnIsSamplesChanged(bool value) { if (value) SetSource("samples"); }
    partial void OnIsFolderChanged(bool value) { if (value) SetSource("folder"); }
    partial void OnIsNpyChanged(bool value) { if (value) SetSource("npy"); }

    private void SetSource(string source)
    {
        if (_loading || _project.Options.Calibration.Source == source)
            return;
        var s = _project.Options.Calibration;
        var kindChanged = (s.Source == "npy") != (source == "npy");
        s.Source = source;
        s.SourcePath = null; // a picture folder is no NPY folder
        if (kindChanged)
            _services.Data.ClearTestImages(_project);
        UpdateFolderText();
        if (source == "samples")
        {
            _ = RestageAsync(userChange: true);
        }
        else
        {
            // nothing usable until a folder is chosen
            Thumbs.Clear();
            Warnings.Clear();
            MoreText = "";
            SummaryText = "폴더를 골라 주세요";
            SummaryKind = ChipKind.Warn;
            _shell.MarkSettingsChanged();
        }
        _ = LoadTestImagesAsync(ensureSample: true);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var title = IsNpy ? "NPY 파일이 있는 폴더 고르기" : "캘리브레이션 사진 폴더 고르기";
        if (_services.Dialogs.PickFolder(title, _project.Options.Calibration.SourcePath) is not { } folder)
            return;
        _project.Options.Calibration.SourcePath = folder;
        UpdateFolderText();
        await RestageAsync(userChange: true);
    }

    [RelayCommand]
    private async Task ApplyCountAsync()
    {
        if (!int.TryParse(CountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 2000)
        {
            Error = "장수는 1에서 2000 사이의 숫자로 적어 주세요.";
            return;
        }
        if (count == _project.Options.Calibration.Count)
            return;
        _project.Options.Calibration.Count = count;
        if (!NeedsFolder)
            await RestageAsync(userChange: true);
    }

    /// <summary>Copies the calibration set into the project again and shows what is there.</summary>
    private async Task RestageAsync(bool userChange)
    {
        if (IsBusy || NeedsFolder)
            return;
        IsBusy = true;
        Error = "";
        SummaryText = "복사하는 중";
        SummaryKind = ChipKind.Running;
        try
        {
            var summary = await Task.Run(() => _services.Data.StageCalibration(_project));
            await Show(summary);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            SummaryText = "가져오지 못했어요";
            SummaryKind = ChipKind.Bad;
        }
        finally
        {
            IsBusy = false;
        }
        if (userChange)
            _shell.MarkSettingsChanged();
    }

    private Task Show(CalibrationSummary summary)
    {
        var unit = IsNpy ? "개" : "장";
        SummaryText = summary.Count == 0 ? "없음" : $"{summary.Count}{unit} 사용";
        SummaryKind = summary.Count == 0 ? ChipKind.Bad : summary.Count < DataPreparer.RecommendedMinimum ? ChipKind.Warn : ChipKind.Ok;
        Warnings.Clear();
        foreach (var w in summary.Warnings)
            Warnings.Add(w);
        MoreText = summary.Count > MaxThumbs ? $"외 {summary.Count - MaxThumbs}{unit}" : "";
        return LoadThumbsAsync(summary.Files.Take(MaxThumbs).ToList());
    }

    private async Task LoadThumbsAsync(IReadOnlyList<string> files)
    {
        var npy = IsNpy;
        var thumbs = await Task.Run(() => files.Select(f => new Thumb(f, System.IO.Path.GetFileName(f), npy ? null : ImageLoader.Thumbnail(f, 120))).ToList());
        Thumbs.Clear();
        foreach (var t in thumbs)
            Thumbs.Add(t);
    }

    // ---- the pictures used to check the result
    private async Task LoadTestImagesAsync(bool ensureSample)
    {
        try
        {
            var npy = IsNpy;
            var thumbs = await Task.Run(() =>
            {
                var files = ensureSample ? _services.Data.EnsureTestImages(_project) : _services.Data.TestImages(_project);
                return files.Select(f => new Thumb(f, System.IO.Path.GetFileName(f), npy ? null : ImageLoader.Thumbnail(f, 160))).ToList();
            });
            TestImages.Clear();
            foreach (var t in thumbs)
                TestImages.Add(t);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddTestImagesAsync()
    {
        var files = _services.Dialogs.PickImages();
        if (files.Count > 0)
            await AddTestFilesAsync(files);
    }

    /// <summary>Adds pictures chosen or dropped by the user.</summary>
    public async Task AddTestFilesAsync(IReadOnlyList<string> files)
    {
        if (!CanEditTest || files.Count == 0)
            return;
        try
        {
            var added = await Task.Run(() => _services.Data.AddTestImages(_project, files));
            Error = added == 0 ? "새로 넣은 사진이 없어요. 이미 있는 사진이거나 사진 파일이 아닙니다." : "";
            if (added > 0)
                _shell.MarkSettingsChanged();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        await LoadTestImagesAsync(ensureSample: false);
    }

    [RelayCommand]
    private async Task RemoveTestImageAsync(Thumb? thumb)
    {
        if (thumb is null || !CanEditTest)
            return;
        if (TestImages.Count <= 1)
        {
            Error = "결과를 확인하려면 사진이 한 장은 있어야 해요.";
            return;
        }
        try
        {
            _services.Data.RemoveTestImage(_project, thumb.Path);
            _shell.MarkSettingsChanged();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        await LoadTestImagesAsync(ensureSample: false);
    }

    [RelayCommand]
    private void Back() => _shell.NavigateTo("config");

    [RelayCommand]
    private void Next() => _shell.NavigateTo("build");
}
