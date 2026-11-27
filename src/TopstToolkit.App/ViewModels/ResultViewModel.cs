using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Results;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.App.ViewModels;

/// <summary>One check picture with what the board network (INT8) and the original network (FP32) found in it.</summary>
public partial class ResultImage : ObservableObject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public ImageSource? Thumb { get; init; }
    public SimImageResult? Int8 { get; init; }
    public SimImageResult? Fp32 { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A class that can be shown or hidden on the picture.</summary>
public partial class ClassToggle(int classNumber, string name, Brush swatch) : ObservableObject
{
    public int ClassNumber { get; } = classNumber;
    public string Name { get; } = name;
    public Brush Swatch { get; } = swatch;
    [ObservableProperty] private bool _isOn = true;
}

/// <summary>One line of the comparison table.</summary>
/// <param name="DeltaKind">down, up, same, new or lost — decides the colour of <paramref name="DeltaText"/>.</param>
public sealed record ResultRow(Brush Swatch, string Name, string ScoreText, string DeltaText, string DeltaKind);

/// <summary>
/// Step 5 "결과": the check pictures with the boxes drawn by the app, the board network next to the original one,
/// and what quantization changed (design §5.5). Layout-independent.
/// </summary>
public partial class ResultViewModel : ObservableObject, IPageViewModel
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x3D, 0x8A, 0xED), Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0x1E, 0x9E, 0x75), Color.FromRgb(0x9B, 0x5D, 0xE5),
        Color.FromRgb(0xE0, 0x45, 0x7B), Color.FromRgb(0x0E, 0xA5, 0xB7), Color.FromRgb(0x7C, 0x8A, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C),
    ];

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private IReadOnlyList<string>? _displayNames;
    private IReadOnlyList<DetectionMatch> _matches = [];
    private readonly Dictionary<int, Color> _classColors = new();

    public ResultViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
    }

    public ObservableCollection<ResultImage> Images { get; } = [];
    public ObservableCollection<ClassToggle> Classes { get; } = [];
    public ObservableCollection<ResultRow> Rows { get; } = [];
    public ObservableCollection<FactRow> Facts { get; } = [];

    /// <summary>Completes when the page has finished reading results; screen captures wait for it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private ResultImage? _selected;
    [ObservableProperty] private BitmapSource? _picture;
    [ObservableProperty] private string _caption = "";

    // what kind of answer the selected picture has
    [ObservableProperty] private bool _isDetection;
    [ObservableProperty] private bool _isClassification;
    [ObservableProperty] private bool _isTensor;
    [ObservableProperty] private bool _hasFp32;

    // which network is on screen
    [ObservableProperty] private bool _isInt8 = true;
    [ObservableProperty] private bool _isFp32;
    [ObservableProperty] private bool _isBoth;
    [ObservableProperty] private IReadOnlyList<BoxOverlay> _primaryBoxes = [];
    [ObservableProperty] private IReadOnlyList<BoxOverlay> _secondaryBoxes = [];
    [ObservableProperty] private string _primaryCaption = "";
    [ObservableProperty] private string _secondaryCaption = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1.0;

    public string ZoomText => $"{Zoom * 100:0}%";

    [ObservableProperty] private string _calloutText = "";
    [ObservableProperty] private bool _isCalloutWarning;

    // the headline number
    [ObservableProperty] private bool _hasTime;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _fpsText = "";

    public void OnShown() => Ready = ReloadAsync();

    private async Task ReloadAsync()
    {
        var keep = Selected?.Name;
        IsStale = _project.Headline is { IsBuilt: true, NeedsRebuild: true };
        var paths = BuildPaths.For(_services.Projects, _project);
        HasTime = _project.Headline.InferenceTimeMs is not null;
        if (_project.Headline.InferenceTimeMs is { } ms)
        {
            TimeText = ms.ToString("0.0", CultureInfo.InvariantCulture);
            FpsText = $"약 {1000 / ms:0} FPS";
        }

        List<ResultImage> images = [];
        if (paths is not null && _project.Headline.IsBuilt)
        {
            var testFiles = _services.Data.TestImages(_project);
            images = await Task.Run(() =>
            {
                _displayNames = LabelImporter.DisplayNames(_services.Projects, _project);
                var int8 = ProjectBuilder.LoadResults(paths.Int8ResultDir).ToDictionary(r => System.IO.Path.GetFileName(r.ResultDir), StringComparer.OrdinalIgnoreCase);
                var fp32 = ProjectBuilder.LoadResults(paths.Fp32ResultDir).ToDictionary(r => System.IO.Path.GetFileName(r.ResultDir), StringComparer.OrdinalIgnoreCase);
                return testFiles.Select(file =>
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                    return new ResultImage
                    {
                        Name = System.IO.Path.GetFileName(file), Path = file, Thumb = ImageLoader.Thumbnail(file, 184),
                        Int8 = int8.GetValueOrDefault(stem), Fp32 = fp32.GetValueOrDefault(stem),
                    };
                }).Where(i => i.Int8 is not null).ToList();
            });
        }

        Images.Clear();
        foreach (var image in images)
            Images.Add(image);
        HasResults = Images.Count > 0;
        if (!HasResults)
        {
            (EmptyTitle, EmptyText) = _project.Headline.IsBuilt
                ? ("보여 줄 결과가 없어요", "결과 확인용 사진이 바뀌었을 수 있어요. 4 빌드를 다시 실행해 주세요.")
                : ("아직 빌드하지 않았어요", "4 빌드를 끝내면 사진 위에 결과를 보여 드립니다.");
            Selected = null;
            return;
        }
        Selected = Images.FirstOrDefault(i => i.Name == keep) ?? Images[0];
        await ShowSelectedAsync();
    }

    partial void OnSelectedChanged(ResultImage? oldValue, ResultImage? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand]
    private async Task SelectAsync(ResultImage? image)
    {
        if (image is null || ReferenceEquals(image, Selected))
            return;
        Selected = image;
        await ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        if (Selected is not { } image)
            return;
        var path = image.Path;
        Picture = path.EndsWith(".npy", StringComparison.OrdinalIgnoreCase) ? null : await Task.Run(() => ImageLoader.Full(path));
        Zoom = 1.0;

        var int8 = image.Int8?.Parsed;
        var fp32 = image.Fp32?.Parsed;
        HasFp32 = fp32 is not null && fp32.Kind == int8?.Kind;
        if (!HasFp32 && (IsFp32 || IsBoth))
            IsInt8 = true;
        IsDetection = int8?.Kind == SimResultKind.Detection;
        IsClassification = int8?.Kind == SimResultKind.Classification;
        IsTensor = int8?.Kind is SimResultKind.Tensor or SimResultKind.Unrecognized or null;

        Classes.Clear();
        Rows.Clear();
        Facts.Clear();
        _classColors.Clear();
        _matches = [];
        var size = Picture is { } p ? $" · {p.PixelWidth}×{p.PixelHeight}" : "";

        if (IsDetection)
        {
            _matches = ResultComparer.Match(int8!.Detections, HasFp32 ? fp32!.Detections : []);
            foreach (var d in _matches.Select(m => m.Any))
            {
                if (_classColors.ContainsKey(d.ClassNumber))
                    continue;
                var color = Palette[_classColors.Count % Palette.Length];
                _classColors[d.ClassNumber] = color;
                var toggle = new ClassToggle(d.ClassNumber, NameOf(d), Frozen(color));
                toggle.PropertyChanged += (_, _) => UpdateBoxes();
                Classes.Add(toggle);
            }
            // highest board score first; objects the board network lost come last
            foreach (var m in _matches.OrderByDescending(m => m.Int8?.ScorePercent ?? -1))
                Rows.Add(ToRow(m));
            Caption = $"{image.Name}{size} · {int8.Detections.Count}개 검출";
            UpdateDetectionCallout();
        }
        else if (IsClassification)
        {
            var name = ClassName(int8!);
            Facts.Add(new FactRow("보드용 (INT8)", name));
            if (HasFp32)
                Facts.Add(new FactRow("원본 (FP32)", ClassName(fp32!)));
            Caption = $"{image.Name}{size} · {name}";
            var same = !HasFp32 || fp32!.ClassNumber == int8!.ClassNumber;
            IsCalloutWarning = !same;
            CalloutText = !HasFp32 ? "원본 모델의 결과가 없어 비교하지 못했어요."
                : same ? "보드용으로 바꾼 뒤에도 원본과 같은 답을 냅니다."
                : "보드용으로 바꾸면서 답이 달라졌어요. 캘리브레이션 사진을 실제 사용 환경의 사진으로 바꾸면 나아질 수 있습니다.";
        }
        else
        {
            // a model the app cut whose part behind the cut became board code (design §5.8, stage 나): the PC computed the
            // same part from these NPU outputs, so the model's own outputs are shown next to the NPU's
            var tail = image.Int8 is { } run && ProjectBuilder.BoardPostProcessOf(_project) == TopstToolkit.Engine.Board.TailCode.Kind
                ? TailFiles.RunFor(_services.Projects.FoldersOf(_project).TailDir, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(run.ResultDir)))
                : null;
            Facts.Add(new FactRow(tail is null ? "출력 모양" : "NPU 출력 모양", int8?.TensorShape ?? "알 수 없음", Mono: true));
            foreach (var output in tail?.Outputs ?? [])
            {
                Facts.Add(new FactRow("모델 출력 " + output.Name, output.ShapeText, Mono: true));
                Facts.Add(new FactRow("  값 범위", $"{Number(output.Min)} ~ {Number(output.Max)}", Mono: true));
            }
            Caption = image.Name;
            IsCalloutWarning = false;
            CalloutText = tail is null
                ? "이 모델은 출력값을 그대로 냅니다. 숫자는 결과 폴더의 파일에 들어 있어요."
                : "잘라낸 뒤쪽 구간은 보드에서 net.so의 C 코드가 계산합니다. 모델 출력은 같은 계산을 PC에서 해 본 값이에요.";
        }
        UpdateBoxes();
    }

    private static string Number(double? value) => value is { } v ? v.ToString("G3", CultureInfo.InvariantCulture) : "?";

    // ---- which network is shown
    partial void OnIsInt8Changed(bool value) { if (value) UpdateBoxes(); }
    partial void OnIsFp32Changed(bool value) { if (value) UpdateBoxes(); }
    partial void OnIsBothChanged(bool value) { if (value) UpdateBoxes(); }

    private void UpdateBoxes()
    {
        var on = Classes.Where(c => c.IsOn).Select(c => c.ClassNumber).ToHashSet();
        IReadOnlyList<BoxOverlay> Boxes(Func<DetectionMatch, Detection?> side) => _matches
            .Select(side).OfType<Detection>().Where(d => on.Contains(d.ClassNumber))
            .Select(d => new BoxOverlay(d.X1, d.Y1, d.X2, d.Y2, $"{NameOf(d)} {d.ScorePercent:0}%", _classColors[d.ClassNumber]))
            .ToList();

        var int8 = Boxes(m => m.Int8);
        var fp32 = Boxes(m => m.Fp32);
        PrimaryBoxes = IsFp32 ? fp32 : int8;
        SecondaryBoxes = fp32;

        // classification: the answer is written on the picture; which network it is stands above the picture
        var int8Class = IsClassification && Selected?.Int8?.Parsed is { } a ? ClassName(a) : "";
        var fp32Class = IsClassification && Selected?.Fp32?.Parsed is { } b ? ClassName(b) : "";
        PrimaryCaption = IsFp32 ? fp32Class : int8Class;
        SecondaryCaption = fp32Class;
    }

    private ResultRow ToRow(DetectionMatch m)
    {
        var d = m.Any;
        var swatch = Frozen(_classColors[d.ClassNumber]);
        var score = m.Int8 is { } i ? i.ScorePercent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
        var (text, kind) = m switch
        {
            { Int8: null } => ($"원본에만 {m.Fp32!.ScorePercent:0.0}%", "lost"),
            { Fp32: null } => (HasFp32 ? "새로 검출" : "", "new"),
            { ScoreDelta: <= -0.05 } => ($"▼ {-m.ScoreDelta:0.0}", "down"),
            { ScoreDelta: >= 0.05 } => ($"▲ {m.ScoreDelta:0.0}", "up"),
            _ => ("같음", "same"),
        };
        return new ResultRow(swatch, NameOf(d), score, text, kind);
    }

    private void UpdateDetectionCallout()
    {
        if (!HasFp32)
        {
            IsCalloutWarning = false;
            CalloutText = "원본 모델의 결과가 없어 비교하지 못했어요. 아래는 보드용(INT8) 결과입니다.";
            return;
        }
        var lost = _matches.Count(m => m.Int8 is null);
        var paired = _matches.Where(m => m.ScoreDelta is not null).Select(m => m.ScoreDelta!.Value).ToList();
        var averageDrop = paired.Count == 0 ? 0 : -paired.Average();
        if (lost > 0)
        {
            IsCalloutWarning = true;
            CalloutText = $"원본에는 있고 보드용(INT8)에는 없는 검출이 {lost}개 있어요. \"나란히\"로 두 결과를 견주어 보세요. 보드용이 놓친 것이라면 캘리브레이션 사진을 실제 환경의 사진으로 바꾸거나 검출 최소 점수를 낮추면 나아질 수 있습니다.";
        }
        else if (averageDrop >= 5)
        {
            IsCalloutWarning = true;
            CalloutText = "INT8로 바꾸면서 점수가 내려갔어요. 캘리브레이션 사진을 실제 사용 환경의 사진으로 바꾸면 나아질 수 있습니다.";
        }
        else
        {
            IsCalloutWarning = false;
            CalloutText = _matches.Count == 0 ? "이 사진에서는 아무것도 찾지 못했어요. 원본 모델도 같습니다."
                : "보드용으로 바꾼 뒤에도 원본과 거의 같은 결과입니다.";
        }
    }

    /// <summary>The project's own display name, a built-in Korean name, or the toolkit label.</summary>
    private string NameOf(Detection d) =>
        _displayNames is { } names && d.ClassNumber >= 0 && d.ClassNumber < names.Count ? names[d.ClassNumber] : LabelNames.Display(d.Label);

    private string ClassName(SimResult r) =>
        _displayNames is { } names && r.ClassNumber is { } n && n >= 0 && n < names.Count ? names[n] : LabelNames.Display(r.ClassLabel ?? r.ClassNumber?.ToString(CultureInfo.InvariantCulture) ?? "");

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(8, Zoom * 1.25);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(1, Zoom / 1.25);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    /// <summary>The picture the toolkit itself drew, for comparison with the app's drawing.</summary>
    [RelayCommand]
    private void OpenToolkitPicture()
    {
        if (Selected?.Int8?.ImageFile is { } file)
            _services.Dialogs.OpenWithShell(file);
        else if (Selected?.Int8?.ResultDir is { } dir)
            _services.Dialogs.ShowInExplorer(dir);
    }

    [RelayCommand]
    private void OpenResultFolder()
    {
        if (Selected?.Int8?.ResultDir is { } dir)
            _services.Dialogs.ShowInExplorer(dir);
    }

    [RelayCommand]
    private void GoToData() => _shell.NavigateTo("data");

    [RelayCommand]
    private void GoToBuild() => _shell.NavigateTo("build");

    [RelayCommand]
    private void GoToPerf() => _shell.NavigateTo("perf");

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
