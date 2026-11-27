using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.App.ViewModels;

/// <summary>One entry of a select box.</summary>
public sealed record Option(string Id, string Name);

/// <summary>
/// Step 2 "설정": what kind of model it is and how pictures go in. A preset fills everything; the user only checks
/// (design §5.3). Every change is saved at once and marks an existing build as out of date. Layout-independent.
/// The toolkit's finer options (output order, DFL, the cut, the quantization range, the board's box maths) have no
/// fields: the preset, the import and the cut on the graph set them (user feedback 2026-11-23, "최대한 자동화").
/// </summary>
public partial class ConfigViewModel : ObservableObject, IPageViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly double[] ImageNetMean = [0.485, 0.456, 0.406];
    private static readonly double[] ImageNetStd = [0.229, 0.224, 0.225];

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private bool _loading;

    public ConfigViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
        Load();
    }

    public ObservableCollection<Option> Presets { get; } = [];

    public IReadOnlyList<Option> LabelSets { get; } =
    [
        new("coco80", "COCO 80개"), new("voc20", "VOC 20개"), new("imagenet", "ImageNet 1000개"), new("coco91", "COCO 91개 (TFLite)"),
        new("custom", "내 라벨 파일"), new("none", "라벨 없음 (번호로 표시)"),
    ];

    // ---- kind and preset
    [ObservableProperty] private bool _isClass;
    [ObservableProperty] private bool _isObj;
    [ObservableProperty] private bool _isUnknown;
    [ObservableProperty] private Option? _selectedPreset;
    [ObservableProperty] private bool _isRecommended;
    [ObservableProperty] private bool _isAutoFilled;

    // ---- labels
    [ObservableProperty] private bool _showLabels;
    [ObservableProperty] private Option? _selectedLabelSet;
    [ObservableProperty] private string _numClassText = "";
    [ObservableProperty] private string _labelHelp = "";
    [ObservableProperty] private bool _isCustomLabels;

    // ---- image preprocessing
    [ObservableProperty] private bool _isUnit;
    [ObservableProperty] private bool _isImageNet;
    [ObservableProperty] private bool _isSigned;
    [ObservableProperty] private bool _isCustomPre;
    [ObservableProperty] private string _meanText = "";
    [ObservableProperty] private string _stdText = "";
    [ObservableProperty] private string _preprocessHelp = "";
    [ObservableProperty] private bool _usesPictures = true;

    // ---- the file the box maths needs (grid, anchor) or the Darknet weights: made by the app or found next to the
    //      model, so the page asks for it only when the preset needs one and the project has none
    [ObservableProperty] private bool _needsCompanion;
    [ObservableProperty] private string _companionTitle = "";
    [ObservableProperty] private string _companionHelp = "";
    [ObservableProperty] private bool _letterbox;
    [ObservableProperty] private string _letterboxHelp = "";

    // ---- side panel
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _guessTitle = "";
    [ObservableProperty] private string _guessReason = "";
    [ObservableProperty] private string _stageChipText = "";
    [ObservableProperty] private ChipKind _stageChipKind;
    [ObservableProperty] private StepState _stageDot;

    public void OnShown() => Load();

    private PresetGuess Guess() => PresetGuesser.Guess(_project.Model?.Format ?? "", _project.Model?.Facts);

    /// <summary>Project → fields.</summary>
    private void Load()
    {
        // a project whose settings lost the grid or anchor file it holds (made before the fix of 2026-11-22) gets it back
        if (Companions.Restore(_services.Projects, _project))
            _services.Projects.Save(_project);
        _loading = true;
        try
        {
            var task = string.IsNullOrEmpty(_project.Task) ? "unknown" : _project.Task;
            IsClass = task == "class";
            IsObj = task == "obj";
            IsUnknown = task == "unknown";

            Presets.Clear();
            foreach (var p in PresetCatalog.All.Where(p => p.Task == task))
                Presets.Add(new Option(p.Id, p.DisplayName));
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == _project.Preset);

            var guess = Guess();
            IsRecommended = guess.PresetId == _project.Preset && guess.PresetId != "generic-unknown";
            IsAutoFilled = IsRecommended;
            GuessTitle = IsRecommended ? "모델을 살펴보고 채웠어요." : "직접 고른 설정이에요.";
            GuessReason = IsRecommended ? guess.Reason + " 맞는지 확인만 하면 됩니다." : "모델에서 읽은 추정은 다음과 같아요: " + guess.Reason;

            var c = _project.Options.Convert;
            var preset = PresetCatalog.Find(_project.Preset);

            // labels
            ShowLabels = task != "unknown";
            var set = _project.Options.Labels.Set ?? "none";
            SelectedLabelSet = LabelSets.FirstOrDefault(o => o.Id == set) ?? LabelSets[^1];
            IsCustomLabels = set == "custom";
            NumClassText = c.NumClass?.ToString(Inv) ?? "";
            UpdateLabelHelp();

            // preprocessing
            UsesPictures = _project.Options.Calibration.Source != "npy";
            MeanText = Nums(c.Mean);
            StdText = Nums(c.Std);
            IsUnit = c.Mean.All(v => v == 0) && c.Std.All(v => v == 1);
            IsImageNet = Close(c.Mean, ImageNetMean) && Close(c.Std, ImageNetStd);
            IsSigned = c.Mean.All(v => v == 0.5) && c.Std.All(v => v == 0.5);
            IsCustomPre = !IsUnit && !IsImageNet && !IsSigned;
            UpdatePreprocessHelp();

            // companion file: only when it is missing
            var companion = preset?.Companion == "weights" ? _project.Model?.Weights : c.PostProcess;
            NeedsCompanion = preset?.Companion is not null && companion is null;
            (CompanionTitle, CompanionHelp) = preset?.Companion switch
            {
                "grid" => ("박스 계산에 쓰는 파일(.bin)이 없어요",
                           "모델과 함께 받은 .bin 파일을 골라 주세요. 모델과 같은 폴더에 두면 가져올 때 앱이 찾아 넣습니다."),
                "anchor" => ("박스 계산에 쓰는 파일(.anchor)이 없어요",
                             "SSD 모델과 함께 받은 .anchor 파일을 골라 주세요. 모델과 같은 폴더에 두면 가져올 때 앱이 찾아 넣습니다."),
                "weights" => ("가중치 파일(.weights)이 없어요", "설정 파일(.cfg)과 짝인 Darknet 가중치 파일을 골라 주세요."),
                _ => ("", ""),
            };

            Letterbox = c.Letterbox;
            LetterboxHelp = ShellViewModel.InputSizeOf(_project.Model?.Facts) is { } size
                ? $"사진을 늘이지 않고 여백을 채워 {size}으로 맞춥니다."
                : "사진을 늘이지 않고 여백을 채워 입력 크기에 맞춥니다.";

            Error = "";
        }
        finally
        {
            _loading = false;
        }
        UpdateStageChip();
    }

    // ---- kind
    partial void OnIsClassChanged(bool value) { if (value) SetTask("class"); }
    partial void OnIsObjChanged(bool value) { if (value) SetTask("obj"); }
    partial void OnIsUnknownChanged(bool value) { if (value) SetTask("unknown"); }

    private void SetTask(string task)
    {
        if (_loading || _project.Task == task)
            return;
        var guess = Guess();
        var preset = PresetCatalog.Find(guess.PresetId) is { } g && g.Task == task ? g : PresetCatalog.All.First(p => p.Task == task);
        ApplyPreset(preset);
    }

    partial void OnSelectedPresetChanged(Option? value)
    {
        if (!_loading && value is not null && value.Id != _project.Preset && PresetCatalog.Find(value.Id) is { } preset)
            ApplyPreset(preset);
    }

    /// <summary>Fills every value from the preset, then puts back what the model itself told us (class count, tensor order).</summary>
    private void ApplyPreset(ModelPreset preset)
    {
        var oldCompanion = PresetCatalog.Find(_project.Preset)?.Companion;
        preset.ApplyTo(_project);
        var guess = Guess();
        if (guess.PresetId == preset.Id)
        {
            if (guess.NumClass is { } classes && classes != preset.NumClass)
            {
                _project.Options.Convert.NumClass = classes;
                _project.Options.Labels.Set = "custom";
            }
            if (guess.OutputOrder is not null)
                _project.Options.Convert.OutputOrder = guess.OutputOrder;
        }
        if (preset.Companion != oldCompanion)
            _project.Options.Convert.PostProcess = null; // a grid file is no anchor file
        // back to the detector: its grid (made by the app at import) or anchor file is still in the project
        Companions.Restore(_services.Projects, _project);
        _project.Options.Labels.Toolkit = null;
        _project.Options.Labels.Display = null;
        _shell.MarkSettingsChanged();
        Load();
    }

    // ---- preprocessing
    partial void OnIsUnitChanged(bool value) { if (value) SetPreprocess([0, 0, 0], [1, 1, 1]); }
    partial void OnIsImageNetChanged(bool value) { if (value) SetPreprocess(ImageNetMean, ImageNetStd); }
    partial void OnIsSignedChanged(bool value) { if (value) SetPreprocess([0.5, 0.5, 0.5], [0.5, 0.5, 0.5]); }
    partial void OnIsCustomPreChanged(bool value) { if (!_loading) UpdatePreprocessHelp(); }

    private void SetPreprocess(double[] mean, double[] std)
    {
        if (_loading)
            return;
        _loading = true;
        MeanText = Nums(mean);
        StdText = Nums(std);
        _loading = false;
        UpdatePreprocessHelp();
        Commit();
    }

    private void UpdatePreprocessHelp() => PreprocessHelp =
        !UsesPictures ? "NPY 데이터는 전처리 없이 그대로 넣습니다."
        : IsUnit ? "픽셀값을 255로 나눕니다 (mean 0, std 1)"
        : IsImageNet ? "ImageNet의 평균과 표준편차로 맞춥니다"
        : IsSigned ? "픽셀값을 -1 ~ 1로 맞춥니다 (mean 0.5, std 0.5)"
        : "(픽셀 ÷ 255 − mean) ÷ std 로 계산합니다. R, G, B 순서로 값 3개를 쉼표로 나눠 적습니다.";

    // ---- every other field: validate, write, save
    partial void OnMeanTextChanged(string value) => Commit();
    partial void OnStdTextChanged(string value) => Commit();
    partial void OnNumClassTextChanged(string value) => Commit();
    partial void OnSelectedLabelSetChanged(Option? value) => Commit();
    partial void OnLetterboxChanged(bool value) => Commit();

    /// <summary>Fields → project. A field that does not parse keeps its old value and is named in <see cref="Error"/>.</summary>
    private void Commit()
    {
        if (_loading)
            return;
        var before = Snapshot();
        var c = _project.Options.Convert;
        var problems = new List<string>();

        if (TryNumbers(MeanText, 3, out var mean) && TryNumbers(StdText, 3, out var std) && std.All(v => v != 0))
        {
            c.Mean = mean;
            c.Std = std;
        }
        else
        {
            problems.Add("이미지 전처리 값은 숫자 3개를 쉼표로 나눠 적어 주세요. std는 0이 될 수 없어요.");
        }

        if (NumClassText.Trim().Length == 0)
            c.NumClass = null;
        else if (int.TryParse(NumClassText.Trim(), NumberStyles.Integer, Inv, out var classes) && classes > 0)
            c.NumClass = classes;
        else
            problems.Add("클래스 수는 1 이상의 숫자로 적어 주세요.");

        if (SelectedLabelSet is { } set)
        {
            IsCustomLabels = set.Id == "custom";
            if (set.Id != "custom")
            {
                _project.Options.Labels.Set = set.Id == "none" ? null : set.Id;
                _project.Options.Labels.Toolkit = null;
                _project.Options.Labels.Display = null;
            }
            else
            {
                _project.Options.Labels.Set = "custom";
            }
        }

        c.Letterbox = Letterbox;

        Error = problems.FirstOrDefault() ?? "";
        if (Snapshot() != before)
        {
            _shell.MarkSettingsChanged();
            UpdateLabelHelp();
            UpdateStageChip();
        }
    }

    private string Snapshot() => JsonSerializer.Serialize(_project.Options);

    // ---- labels
    private void UpdateLabelHelp()
    {
        var labels = _project.Options.Labels;
        if (labels.Set is null)
        {
            LabelHelp = "이름 없이 클래스 번호로 표시합니다.";
            return;
        }
        if (labels.Set == "custom" && labels.Toolkit is null)
        {
            LabelHelp = "라벨 파일을 넣으면 이름으로 표시합니다. 지금은 번호로 표시해요.";
            return;
        }
        IReadOnlyList<string> names = LabelImporter.DisplayNames(_services.Projects, _project)
                                      ?? (_services.Packager.LabelsOf(_project) is { } file ? LabelImporter.ReadNames(file) : []);
        var preview = string.Join(", ", names.Take(3).Select(LabelNames.Display));
        LabelHelp = names.Count == 0 ? "" : $"{preview} … ({names.Count}개)";
    }

    [RelayCommand]
    private void PickLabels()
    {
        if (_services.Dialogs.PickFile("라벨 파일 고르기", "라벨 파일 (*.txt;*.label;*.names)|*.txt;*.label;*.names|모든 파일 (*.*)|*.*") is not { } file)
            return;
        try
        {
            var result = LabelImporter.Import(_services.Projects, _project, file);
            Error = _project.Options.Convert.NumClass is { } n && n != result.Count
                ? $"라벨은 {result.Count}줄인데 클래스 수는 {n}개예요. 둘이 같아야 이름이 맞게 붙습니다."
                : "";
            if (_project.Options.Convert.NumClass is null)
                _project.Options.Convert.NumClass = result.Count;
            _shell.MarkSettingsChanged();
            var keepError = Error;
            Load();
            Error = keepError;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    // ---- the companion file
    [RelayCommand]
    private void PickCompanion()
    {
        var companion = PresetCatalog.Find(_project.Preset)?.Companion;
        var (title, filter) = companion switch
        {
            "grid" => ("박스 계산 파일 고르기", "YOLO 박스 계산 파일 (*.bin)|*.bin|모든 파일 (*.*)|*.*"),
            "anchor" => ("박스 계산 파일 고르기", "SSD 박스 계산 파일 (*.anchor)|*.anchor|모든 파일 (*.*)|*.*"),
            "weights" => ("가중치 파일 고르기", "Darknet 가중치 (*.weights)|*.weights|모든 파일 (*.*)|*.*"),
            _ => ("", null),
        };
        if (filter is null || _services.Dialogs.PickFile(title, filter) is not { } file)
            return;
        try
        {
            var folders = _services.Projects.FoldersOf(_project);
            var staged = FileStager.StageFile(file, folders.ModelInputDir);
            var relative = _services.Projects.ToRelative(_project, staged.Staged);
            if (companion == "weights")
                _project.Model!.Weights = relative;
            else
                _project.Options.Convert.PostProcess = relative;
            _shell.MarkSettingsChanged();
            Load();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    private void UpdateStageChip()
    {
        var h = _project.Headline;
        (StageChipText, StageChipKind, StageDot) = !h.IsBuilt ? ("아직 빌드 전", ChipKind.Muted, StepState.Todo)
            : h.NeedsRebuild ? ("다시 필요", ChipKind.Warn, StepState.Warn)
            : ("최신", ChipKind.Ok, StepState.Ok);
    }

    [RelayCommand]
    private void Back() => _shell.NavigateTo("model");

    [RelayCommand]
    private void Next() => _shell.NavigateTo("data");

    // ---- small parsers
    private static string Nums(double[] values) => string.Join(", ", values.Select(v => v.ToString("0.########", Inv)));

    private static bool Close(double[] a, double[] b) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) < 1e-6);

    /// <summary>"0.5" (used for every channel) or "0.485, 0.456, 0.406".</summary>
    private static bool TryNumbers(string text, int count, out double[] values)
    {
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<double>();
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, Inv, out var v) || double.IsNaN(v) || double.IsInfinity(v))
            {
                values = [];
                return false;
            }
            parsed.Add(v);
        }
        if (parsed.Count == 1 && count > 1)
            parsed = Enumerable.Repeat(parsed[0], count).ToList();
        values = [.. parsed];
        return parsed.Count == count;
    }
}
