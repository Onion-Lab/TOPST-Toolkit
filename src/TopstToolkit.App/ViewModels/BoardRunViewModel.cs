using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Board;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.App.ViewModels;

public enum PhotoState
{
    Todo,
    Running,
    Done,
    Failed,
}

/// <summary>One picture of a run on the board: its thumbnail, how far it is, and what the board found.</summary>
public partial class BoardPhoto : ObservableObject
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public ImageSource? Thumb { get; init; }
    [ObservableProperty] private PhotoState _state;
    [ObservableProperty] private string _badge = "대기";
    [ObservableProperty] private bool _isSelected;
    public BoardRunFrame? Frame { get; set; }
    /// <summary>What the PC's simulation found in the same picture (the project's check pictures only).</summary>
    public SimResult? Pc { get; set; }
}

/// <summary>One line of "PC 결과와 비교".</summary>
public sealed record BoardCompareRow(Brush Swatch, string Name, string Board, string Pc);

/// <summary>What runs on the board: a project's model (put there first) or one already on the board.</summary>
public sealed record BoardRunTarget(string RemoteDir, string Title, bool Letterbox, IReadOnlyList<string>? Names, double? PcEstimateMs);

/// <summary>
/// 보드 › 실행 (docs/mockups/mockup-board.html ③ ④): the open project's model — or one already on the board — runs on
/// one NPU with the user's pictures or a video. Frames are made the network's size on the PC before they go
/// (the toolkit's letterbox and resize), the board's boxes come back and the app draws them.
/// </summary>
public partial class BoardRunViewModel : ObservableObject
{
    public const string VideoFilter = "영상 (*.mp4;*.avi;*.mov;*.mkv;*.wmv)|*.mp4;*.avi;*.mov;*.mkv;*.wmv|모든 파일 (*.*)|*.*";

    /// <summary>Previews kept for scrubbing after a run; longer videos keep every n-th.</summary>
    private const int MaxPreviews = 1500;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x3D, 0x8A, 0xED), Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0x1E, 0x9E, 0x75), Color.FromRgb(0x9B, 0x5D, 0xE5),
        Color.FromRgb(0xE0, 0x45, 0x7B), Color.FromRgb(0x0E, 0xA5, 0xB7), Color.FromRgb(0x7C, 0x8A, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C),
    ];

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly BoardViewModel _page;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<int, Color> _colors = new();
    private readonly Dictionary<string, string> _deployed = new();
    private readonly List<BoardRunFrame> _videoFrames = [];
    private BoardModelInfo? _boardModel;
    private CancellationTokenSource? _run;
    private IReadOnlyList<string>? _names;
    private VideoInfo? _video;
    private int _videoEvery = 1;
    private DispatcherTimer? _player;

    public BoardRunViewModel(AppServices services, ShellViewModel shell, BoardViewModel page)
    {
        _services = services;
        _shell = shell;
        _page = page;
    }

    private BoardConnection Board => _shell.Board;

    public void OnShown() => RefreshTarget();

    public void OnBoardChanged()
    {
        if (IsVideoRunning && Board.Status is { } s && Board.RunStartTemperature is { } start)
            TemperatureText = $"{start:0} → {s.MaxTemperature:0}°C";
    }

    /// <summary>Completes when the last run (or picture) finished loading; screen captures wait for it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- what runs
    [ObservableProperty] private bool _hasTarget;
    [ObservableProperty] private string _targetTitle = "";
    [ObservableProperty] private string _targetCaption = "";
    [ObservableProperty] private bool _isBoardModel;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _sizeNote = "";

    public void RefreshTarget()
    {
        if (_boardModel is { } m)
        {
            HasTarget = true;
            IsBoardModel = true;
            TargetTitle = BoardManageViewModel.TitleOf(m);
            TargetCaption = m.ByApp ? "보드에 있는 모델" : "보드 기본 모델";
            SizeNote = m.Input is [var w, var h] ? $"{w}×{h}으로 줄여서 보내요" : "";
            CanUseTestPhotos = false;
            return;
        }
        IsBoardModel = false;
        var project = _shell.Project;
        if (project?.Model is null)
        {
            HasTarget = false;
            EmptyTitle = "실행할 모델이 없어요";
            EmptyText = "프로젝트를 열어 빌드하거나, 보드에 있는 모델을 고르세요.";
            return;
        }
        if (!project.Headline.IsBuilt || BoardProjects.FilesFor(_services.Projects, project) is null)
        {
            HasTarget = false;
            EmptyTitle = "아직 빌드하지 않았어요";
            EmptyText = "4 빌드를 끝내면 보드에 올려 실행할 수 있어요.";
            return;
        }
        HasTarget = true;
        TargetTitle = ShellViewModel.ModelTitle(project);
        TargetCaption = _deployed.TryGetValue(project.Id, out var state) ? state : "실행하면 보드에 올려요";
        SizeNote = ShellViewModel.InputSizeOf(project.Model.Facts) is { } size ? $"{size}으로 줄여서 보내요" : "";
        CanUseTestPhotos = _services.Data.TestImages(project).Count > 0;
    }

    /// <summary>Runs a model that is already on the board (보드에 있는 모델 › 실행).</summary>
    public void UseBoardModel(BoardModelInfo model)
    {
        _boardModel = model;
        ClearPhotos();
        RefreshTarget();
    }

    [RelayCommand]
    private void UseProjectModel()
    {
        _boardModel = null;
        ClearPhotos();
        RefreshTarget();
    }

    [RelayCommand]
    private void ChooseBoardModel() => _page.IsManageTab = true;

    /// <summary>
    /// The model on the board, ready to run: a project's model goes there first unless the same files are there
    /// (BoardAgent.DeployAsync), after the checks (NPU memory, storage, the board's NPU library). Null when it cannot run;
    /// <see cref="Error"/> says why.
    /// </summary>
    public async Task<BoardRunTarget?> PrepareTargetAsync(CancellationToken ct)
    {
        if (!Board.IsConnected || Board.Agent is not { } agent || Board.Status is not { } status)
        {
            Error = "보드에 연결돼 있지 않아요";
            return null;
        }
        if (_boardModel is { } m)
            return await Task.Run(() => BoardModelTarget(m), ct);
        var project = _shell.Project;
        if (project?.Model is null || !project.Headline.IsBuilt)
        {
            Error = "먼저 4 빌드를 끝내 주세요";
            return null;
        }
        var title = ShellViewModel.ModelTitle(project);
        // hashing the files and reading the names takes a moment for a big model: not on the screen's thread
        if (await Task.Run(() => BoardProjects.Deployment(_services.Projects, project, title, _services.EngineLayout), ct) is not { } deployment)
        {
            Error = "빌드 결과에 보드용 파일이 없어요. 다시 빌드해 주세요.";
            return null;
        }
        var checks = BoardAgent.Checks(status, BoardProjects.NpuBytesNeeded(_services.Projects, project), deployment.Bytes, false);
        Checks.Clear();
        foreach (var check in checks)
            Checks.Add(check);
        HasChecks = true;
        if (checks.FirstOrDefault(c => !c.Ok) is { } bad)
        {
            Error = $"{bad.Text} · {bad.Detail}";
            return null;
        }
        TargetCaption = "보드에 올리는 중…";
        var deployed = await agent.DeployAsync(deployment, ct);
        _deployed[project.Id] = deployed.Skipped ? "보드에 있음" : "보드에 올림 · 방금";
        TargetCaption = _deployed[project.Id];
        if (!deployed.Skipped)
            _shell.PostLog($"보드에 올림 {deployed.RemoteDir} · {BoardAgent.Mb(deployed.Bytes)}");
        return new BoardRunTarget(deployed.RemoteDir, title, project.Options.Convert.Letterbox, deployment.Manifest.Labels, project.Headline.InferenceTimeMs);
    }

    /// <summary>
    /// A model that is on the board already: the project it came from gives its letterbox and names when that project
    /// is in this workspace; otherwise its manifest does (the app writes both into topst.json); the board's own two
    /// samples (the toolkit's YOLOv5s and MobileNet-v2) get COCO and ImageNet names.
    /// </summary>
    private BoardRunTarget BoardModelTarget(BoardModelInfo m)
    {
        var title = BoardManageViewModel.TitleOf(m);
        if (m.Manifest?.Project is { Length: > 0 } id && _services.Projects.Load(id) is { } project)
            return new BoardRunTarget(m.Path, title, project.Options.Convert.Letterbox,
                BoardProjects.ClassNames(_services.Projects, project, _services.EngineLayout), project.Headline.InferenceTimeMs);
        if (m.Manifest is { Labels.Count: > 0 } manifest)
            return new BoardRunTarget(m.Path, title, manifest.Letterbox ?? m.Type == "obj", manifest.Labels, null);
        IReadOnlyList<string>? names = null;
        var set = m.ByApp ? null : m.Type == "class" ? "imagenet" : m.Name.Contains("yolo", StringComparison.OrdinalIgnoreCase) ? "coco80" : null;
        if (_services.EngineLayout is { } layout && ProjectBuilder.LabelFileName(set) is { } file && File.Exists(Path.Combine(layout.LabelsDir, file)))
            names = TopstToolkit.Core.Projects.LabelImporter.ReadNames(Path.Combine(layout.LabelsDir, file)).Select(LabelNames.Display).ToList();
        return new BoardRunTarget(m.Path, title, m.Type == "obj", names, null);
    }

    // ---------------------------------------------------------------- input and NPU
    [ObservableProperty] private bool _isPhotoMode = true;
    [ObservableProperty] private bool _isVideoMode;
    [ObservableProperty] private bool _isNpu0 = true;
    [ObservableProperty] private bool _isNpu1;

    public int Npu => IsNpu1 ? 1 : 0;

    partial void OnIsPhotoModeChanged(bool value)
    {
        if (value) IsVideoMode = false;
    }

    partial void OnIsVideoModeChanged(bool value)
    {
        if (value) IsPhotoMode = false;
    }

    partial void OnIsNpu0Changed(bool value)
    {
        if (value) IsNpu1 = false;
    }

    partial void OnIsNpu1Changed(bool value)
    {
        if (value) IsNpu0 = false;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunVideoCommand), nameof(PickPhotosCommand), nameof(UseTestPhotosCommand), nameof(PickVideoCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private void Stop() => _run?.Cancel();

    // ---------------------------------------------------------------- pictures
    public ObservableCollection<BoardPhoto> Photos { get; } = [];
    public ObservableCollection<BoardCheck> Checks { get; } = [];
    public ObservableCollection<BoardCompareRow> CompareRows { get; } = [];

    [ObservableProperty] private bool _hasPhotos;
    [ObservableProperty] private BoardPhoto? _selectedPhoto;
    [ObservableProperty] private BitmapSource? _picture;
    [ObservableProperty] private IReadOnlyList<BoxOverlay> _boxes = [];
    [ObservableProperty] private string _pictureTag = "";
    [ObservableProperty] private string _hudTime = "";
    [ObservableProperty] private bool _hasChecks;
    [ObservableProperty] private bool _hasAverage;
    [ObservableProperty] private string _averageText = "";
    [ObservableProperty] private string _pcEstimateText = "";
    [ObservableProperty] private bool _hasCompare;
    [ObservableProperty] private string _compareChip = "";
    [ObservableProperty] private ChipKind _compareKind = ChipKind.Ok;
    [ObservableProperty] private bool _canUseTestPhotos;
    [ObservableProperty] private string _photoSaveText = "";

    private bool CanPick() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickPhotosAsync()
    {
        var files = _services.Dialogs.PickImages();
        if (files.Count > 0)
            await RunPhotosAsync(files);
    }

    /// <summary>"검사 사진으로": the project's own check pictures, which the PC's results can be compared with.</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task UseTestPhotosAsync()
    {
        if (_shell.Project is { } project)
            await RunPhotosAsync(_services.Data.TestImages(project));
    }

    [RelayCommand]
    private void SelectPhoto(BoardPhoto? photo)
    {
        if (photo is not null)
            SelectedPhoto = photo;
    }

    partial void OnSelectedPhotoChanged(BoardPhoto? value)
    {
        foreach (var p in Photos)
            p.IsSelected = ReferenceEquals(p, value);
        Ready = ShowPhotoAsync(value);
    }

    private void ClearPhotos()
    {
        Photos.Clear();
        HasPhotos = false;
        SelectedPhoto = null;
        Picture = null;
        Boxes = [];
        HasAverage = HasCompare = HasChecks = false;
        CompareRows.Clear();
        Checks.Clear();
        PhotoSaveText = "";
    }

    public async Task RunPhotosAsync(IReadOnlyList<string> files)
    {
        if (IsRunning || Board.IsRunning || files.Count == 0)
            return;
        if (_services.EngineLayout is not { } layout)
        {
            Error = "엔진이 없어 사진을 준비할 수 없어요. 홈에서 구성 요소를 설치해 주세요.";
            return;
        }
        Error = PhotoSaveText = "";
        var rows = await Task.Run(() => files.Select(f => new BoardPhoto { Path = f, Thumb = ImageLoader.Thumbnail(f, 168) }).ToList());
        foreach (var row in rows)
            Photos.Add(row);
        HasPhotos = true;
        SelectedPhoto ??= rows[0];

        _run = new CancellationTokenSource();
        var ct = _run.Token;
        IsRunning = true;
        Board.BeginRun();
        _shell.BeginTask("보드에서 실행 중", $"사진 {files.Count}장");
        var cancelled = false;
        try
        {
            if (await PrepareTargetAsync(ct) is not { } target)
            {
                _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
                return;
            }
            _names = target.Names;
            PcEstimateText = target.PcEstimateMs is { } pc ? $"PC 추정 {pc.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "";
            await Task.Run(() => AttachPcResults(rows), ct);
            rows[0].State = PhotoState.Running;
            rows[0].Badge = "실행 중";
            var runner = new BoardRunner(Board.Ssh!, layout);
            var summary = await runner.RunAsync(target.RemoteDir, Npu, target.Letterbox, files, null, 1,
                frame => _ui.Invoke(() => OnPhotoFrame(frame, rows)), ct: ct);
            _shell.EndTask("보드 실행 끝", $"사진 {summary.Frames}장 · 평균 {summary.AverageNpuMs.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _shell.EndTask("보드 실행을 멈췄어요", "", StepState.Todo);
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
            if (ex.Detail is { Length: > 0 } d)
                _shell.PostLog("보드: " + d.Trim());
            _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            Error = "사진을 보드로 보내지 못했어요";
            _shell.PostLog("보드: " + ex.Message);
            _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
        }
        finally
        {
            foreach (var row in rows.Where(r => r.State is PhotoState.Todo or PhotoState.Running))
            {
                row.State = PhotoState.Failed;
                row.Badge = cancelled ? "멈춤" : "실패";
            }
            IsRunning = false;
            Board.EndRun();
            UpdateAverage();
        }
    }

    private void OnPhotoFrame(BoardRunFrame frame, IReadOnlyList<BoardPhoto> rows)
    {
        if (frame.Info.Index < 0 || frame.Info.Index >= rows.Count)
            return;
        var row = rows[frame.Info.Index];
        row.Frame = frame;
        row.State = PhotoState.Done;
        row.Badge = frame.Result.ClassId is { } cls ? NameOf("class", cls) : $"{frame.Boxes.Count}개";
        if (frame.Info.Index + 1 < rows.Count && rows[frame.Info.Index + 1] is { State: PhotoState.Todo } next)
        {
            next.State = PhotoState.Running;
            next.Badge = "실행 중";
        }
        if (ReferenceEquals(SelectedPhoto, row))
            Ready = ShowPhotoAsync(row);
        UpdateAverage();
    }

    private void UpdateAverage()
    {
        var done = Photos.Where(p => p.Frame is not null).Select(p => p.Frame!.Result.NpuMs).ToList();
        HasAverage = done.Count > 0;
        AverageText = done.Count > 0 ? done.Average().ToString("0.0", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>The PC's INT8 results for pictures that are the project's own check pictures.</summary>
    private void AttachPcResults(IReadOnlyList<BoardPhoto> rows)
    {
        if (_boardModel is not null || _shell.Project is not { } project || BuildPaths.For(_services.Projects, project) is not { } paths)
            return;
        var tests = _services.Data.TestImages(project).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!rows.Any(r => tests.Contains(Path.GetFullPath(r.Path))))
            return;
        var results = ProjectBuilder.LoadResults(paths.Int8ResultDir)
            .GroupBy(r => Path.GetFileName(r.ResultDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (tests.Contains(Path.GetFullPath(row.Path)) && results.TryGetValue(Path.GetFileNameWithoutExtension(row.Path), out var r))
                row.Pc = r.Parsed;
    }

    private async Task ShowPhotoAsync(BoardPhoto? photo)
    {
        if (photo is null)
        {
            Picture = null;
            Boxes = [];
            HasCompare = false;
            return;
        }
        var picture = await Task.Run(() => ImageLoader.Full(photo.Path));
        if (!ReferenceEquals(SelectedPhoto, photo))
            return;
        Picture = picture;
        if (photo.Frame is { } frame)
        {
            Boxes = frame.Boxes.Select(b => new BoxOverlay(b.X1, b.Y1, b.X2, b.Y2, $"{NameOf("obj", b.ClassNumber)} {b.Score * 100:0}%", ColorOf(b.ClassNumber))).ToList();
            PictureTag = frame.Result.ClassId is { } cls ? NameOf("class", cls) : "";
            HudTime = $"NPU {frame.Result.NpuMs.ToString("0.0", CultureInfo.InvariantCulture)} ms";
        }
        else
        {
            Boxes = [];
            PictureTag = "";
            HudTime = "";
        }
        ShowCompare(photo);
    }

    private void ShowCompare(BoardPhoto photo)
    {
        CompareRows.Clear();
        if (photo.Pc is not { Kind: SimResultKind.Detection } pc || photo.Frame is not { } frame)
        {
            HasCompare = false;
            return;
        }
        var used = new HashSet<Detection>();
        var same = true;
        foreach (var b in frame.Boxes.OrderByDescending(b => b.Score))
        {
            var match = pc.Detections.Where(d => !used.Contains(d) && d.ClassNumber == b.ClassNumber - 1)
                .Select(d => (d, iou: Iou(b, d))).Where(x => x.iou >= 0.45).OrderByDescending(x => x.iou).Select(x => x.d).FirstOrDefault();
            if (match is not null)
            {
                used.Add(match);
                same &= Math.Abs(b.Score * 100 - match.ScorePercent) <= 1.0;
            }
            else
            {
                same = false;
            }
            CompareRows.Add(new BoardCompareRow(Swatch(b.ClassNumber), NameOf("obj", b.ClassNumber), Percent(b.Score * 100),
                match is null ? "—" : Percent(match.ScorePercent)));
        }
        foreach (var d in pc.Detections.Where(d => !used.Contains(d)))
        {
            same = false;
            CompareRows.Add(new BoardCompareRow(Swatch(d.ClassNumber + 1), _names is null ? LabelNames.Display(d.Label) : NameOf("obj", d.ClassNumber + 1), "—", Percent(d.ScorePercent)));
        }
        HasCompare = CompareRows.Count > 0;
        CompareChip = same ? "같아요" : "조금 달라요";
        CompareKind = same ? ChipKind.Ok : ChipKind.Warn;
    }

    private static string Percent(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static double Iou(BoardBox a, Detection b)
    {
        var w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        var h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0)
            return 0;
        var inter = w * h;
        return inter / ((a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter);
    }

    /// <summary>"결과 저장": each picture with the boxes drawn in, and one CSV table of what the board found.</summary>
    [RelayCommand]
    private async Task SavePhotosAsync()
    {
        var done = Photos.Where(p => p.Frame is not null).ToList();
        if (done.Count == 0 || _services.Dialogs.PickFolder("결과를 저장할 폴더", ResultsFolder()) is not { } folder)
            return;
        var names = done.ToDictionary(p => p, p => p.Frame!.Boxes.Select(b => (b, name: NameOf("obj", b.ClassNumber), color: ColorOf(b.ClassNumber))).ToList());
        var count = await Task.Run(() =>
        {
            var csv = new StringBuilder("file,class,name,score,x1,y1,x2,y2,npu_ms\n");
            var written = 0;
            foreach (var photo in done)
            {
                var frame = photo.Frame!;
                var ms = frame.Result.NpuMs.ToString("0.000", CultureInfo.InvariantCulture);
                if (frame.Result.ClassId is { } cls)
                    csv.Append($"{Csv(photo.Name)},{cls},{Csv(NameOfSafe("class", cls))},,,,,,{ms}\n");
                else if (frame.Boxes.Count == 0)
                    csv.Append($"{Csv(photo.Name)},,,,,,,,{ms}\n");
                foreach (var (b, name, _) in names[photo])
                    csv.Append(string.Join(',', Csv(photo.Name), b.ClassNumber, Csv(name), b.Score.ToString("0.0000", CultureInfo.InvariantCulture),
                        F(b.X1), F(b.Y1), F(b.X2), F(b.Y2), ms)).Append('\n');
                if (ImageLoader.Full(photo.Path) is not { } picture)
                    continue;
                var boxes = names[photo].Select(x => new BoxOverlay(x.b.X1, x.b.Y1, x.b.X2, x.b.Y2, $"{x.name} {x.b.Score * 100:0}%", x.color)).ToList();
                var drawn = DetectionView.Render(picture, boxes);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(drawn));
                using var stream = File.Create(Path.Combine(folder, Path.GetFileNameWithoutExtension(photo.Path) + ".board.png"));
                encoder.Save(stream);
                written++;
            }
            File.WriteAllText(Path.Combine(folder, "board-results.csv"), csv.ToString(), new UTF8Encoding(true));
            return written;
        });
        PhotoSaveText = $"사진 {count}장과 표(board-results.csv)를 저장했어요";
        _services.Dialogs.ShowInExplorer(Path.Combine(folder, "board-results.csv"));
    }

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Where the save dialogs start: 내 문서\TOPST Toolkit\보드 결과 (made if it is missing).</summary>
    private string? ResultsFolder() => TopstToolkit.Core.Workspace.SaveFolders.Prepared(TopstToolkit.Core.Workspace.SaveFolders.BoardResults(_services.Settings.SaveRoot));

    private static string Csv(string text) => text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    // ---------------------------------------------------------------- video
    [ObservableProperty] private bool _hasVideo;
    [ObservableProperty] private string _videoPath = "";
    [ObservableProperty] private string _videoTitle = "";
    [ObservableProperty] private string _videoCaption = "";
    [ObservableProperty] private bool _isEveryAll = true;
    [ObservableProperty] private bool _isEvery10;
    [ObservableProperty] private bool _isEvery5;
    [ObservableProperty] private BitmapSource? _videoFrame;
    [ObservableProperty] private IReadOnlyList<BoxOverlay> _videoBoxes = [];
    [ObservableProperty] private string _videoTag = "";
    [ObservableProperty] private string _hudFrame = "";
    [ObservableProperty] private string _hudVideoTime = "";
    [ObservableProperty] private string _hudCount = "";
    [ObservableProperty] private int _doneFrames;
    [ObservableProperty] private int _totalFrames;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _npuAverageText = "—";
    [ObservableProperty] private string _remainText = "—";
    [ObservableProperty] private string _temperatureText = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveVideoResultCommand), nameof(TogglePlayCommand))]
    private bool _isVideoRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveVideoResultCommand), nameof(TogglePlayCommand))]
    private bool _isVideoDone;

    [ObservableProperty] private int _frameIndex;
    [ObservableProperty] private int _frameMax;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _playTimeText = "";
    [ObservableProperty] private bool _saveVideo = true;
    [ObservableProperty] private bool _saveCsv = true;
    [ObservableProperty] private string _videoSaveText = "";

    partial void OnIsEveryAllChanged(bool value)
    {
        if (value) IsEvery10 = IsEvery5 = false;
    }

    partial void OnIsEvery10Changed(bool value)
    {
        if (value) IsEveryAll = IsEvery5 = false;
    }

    partial void OnIsEvery5Changed(bool value)
    {
        if (value) IsEveryAll = IsEvery10 = false;
    }

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickVideoAsync()
    {
        if (_services.Dialogs.PickFile("영상 고르기", VideoFilter) is not { } file)
            return;
        await OpenVideoAsync(file);
    }

    /// <summary>Reads the video's size and length (nothing runs yet).</summary>
    public async Task OpenVideoAsync(string file)
    {
        if (_services.EngineLayout is not { } layout)
        {
            Error = "엔진이 없어 영상을 열 수 없어요. 홈에서 구성 요소를 설치해 주세요.";
            return;
        }
        Error = "";
        try
        {
            var info = await FrameSource.ProbeAsync(layout, file);
            _video = info;
            VideoPath = file;
            VideoTitle = Path.GetFileName(file);
            VideoCaption = Describe(info);
            HasVideo = true;
            ResetVideo();
            RunVideoCommand.NotifyCanExecuteChanged();
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>"1920×1080 · 초당 30장 · 12초 · 360장".</summary>
    public static string Describe(VideoInfo v)
    {
        var parts = new List<string> { $"{v.Width}×{v.Height}", $"초당 {v.Fps.ToString("0.#", CultureInfo.InvariantCulture)}장" };
        if (v.Frames > 0 && v.Fps > 0)
            parts.Add(Clock(v.Frames / v.Fps, words: true));
        if (v.Frames > 0)
            parts.Add($"{v.Frames}장");
        return string.Join(" · ", parts);
    }

    /// <summary>"0:06", or "12초" / "2분 5초" in words.</summary>
    private static string Clock(double seconds, bool words = false)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (!words)
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        return t.TotalMinutes >= 1 ? (t.Seconds > 0 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{(int)t.TotalMinutes}분") : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}초";
    }

    private void ResetVideo()
    {
        StopPlayer();
        lock (_videoFrames)
            _videoFrames.Clear();
        VideoFrame = null;
        VideoBoxes = [];
        VideoTag = HudFrame = HudVideoTime = HudCount = PlayTimeText = VideoSaveText = "";
        DoneFrames = TotalFrames = FrameIndex = FrameMax = 0;
        ProgressFraction = 0;
        ProgressText = "";
        SpeedText = NpuAverageText = RemainText = TemperatureText = "—";
        IsVideoDone = false;
    }

    private bool CanRunVideo() => HasVideo && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunVideo))]
    private async Task RunVideoAsync()
    {
        if (_video is not { } video || IsRunning || Board.IsRunning || _services.EngineLayout is not { } layout)
            return;
        Error = "";
        ResetVideo();
        var every = IsEveryAll ? 1 : Math.Max(1, (int)Math.Round(video.Fps / (IsEvery10 ? 10 : 5)));
        _videoEvery = every;
        var expected = video.Frames > 0 ? (video.Frames + every - 1) / every : 0;
        TotalFrames = expected;
        ProgressText = expected > 0 ? $"0 / {expected}장" : "";
        var stride = Math.Max(1, (int)Math.Ceiling(expected / (double)MaxPreviews));

        _run = new CancellationTokenSource();
        var ct = _run.Token;
        IsRunning = IsVideoRunning = true;
        Board.BeginRun();
        TemperatureText = Board.RunStartTemperature is { } t0 ? $"{t0:0}°C" : "—";
        _shell.BeginTask("영상 실행 중", VideoTitle);
        try
        {
            if (await PrepareTargetAsync(ct) is not { } target)
            {
                _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
                return;
            }
            _names = target.Names;
            var runner = new BoardRunner(Board.Ssh!, layout);
            var watch = Stopwatch.StartNew();
            var done = 0;
            var npuSum = 0.0;
            var lastShown = TimeSpan.FromMinutes(-1);
            var summary = await runner.RunAsync(target.RemoteDir, Npu, target.Letterbox, null, VideoPath, every, frame =>
            {
                done++;
                npuSum += frame.Result.NpuMs;
                var keep = (done - 1) % stride == 0 ? frame : frame with { Preview = [] };
                lock (_videoFrames)
                    _videoFrames.Add(keep);
                // the screen follows at about 12 pictures a second, whatever the speed of the run
                var now = watch.Elapsed;
                BitmapSource? preview = null;
                if (now - lastShown >= TimeSpan.FromMilliseconds(80) && frame.Preview.Length > 0)
                {
                    lastShown = now;
                    preview = Decode(frame.Preview);
                }
                var count = done;
                var average = npuSum / done;
                _ui.BeginInvoke(() => OnVideoFrame(frame, preview, count, average, watch.Elapsed));
            }, ct: ct);
            await _ui.InvokeAsync(() => { }, DispatcherPriority.Background);
            lock (_videoFrames)
                FrameMax = Math.Max(0, _videoFrames.Count - 1);
            IsVideoDone = _videoFrames.Count > 0;
            FrameIndex = FrameMax;
            ShowVideoFrame(FrameIndex);
            ProgressFraction = 1;
            RemainText = "끝";
            _shell.EndTask("영상 실행 끝", $"{summary.Frames}장 · 평균 {summary.AverageNpuMs.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        }
        catch (OperationCanceledException)
        {
            lock (_videoFrames)
                FrameMax = Math.Max(0, _videoFrames.Count - 1);
            IsVideoDone = _videoFrames.Count > 0;
            RemainText = "멈춤";
            _shell.EndTask("영상 실행을 멈췄어요", $"{DoneFrames}장까지", StepState.Todo);
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
            if (ex.Detail is { Length: > 0 } d)
                _shell.PostLog("보드: " + d.Trim());
            _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            Error = "영상을 보드로 보내지 못했어요";
            _shell.PostLog("보드: " + ex.Message);
            _shell.EndTask("보드에서 실행하지 못했어요", "", StepState.Bad);
        }
        finally
        {
            IsRunning = IsVideoRunning = false;
            Board.EndRun();
        }
    }

    private void OnVideoFrame(BoardRunFrame frame, BitmapSource? preview, int done, double npuAverage, TimeSpan elapsed)
    {
        if (!IsVideoRunning)
            return;
        DoneFrames = done;
        ProgressFraction = TotalFrames > 0 ? Math.Min(1, done / (double)TotalFrames) : 0;
        ProgressText = TotalFrames > 0 ? $"{done} / {TotalFrames}장" : $"{done}장";
        var rate = done / Math.Max(elapsed.TotalSeconds, 1e-3);
        SpeedText = $"초당 {rate.ToString("0.0", CultureInfo.InvariantCulture)}장";
        NpuAverageText = $"평균 {npuAverage.ToString("0.0", CultureInfo.InvariantCulture)} ms";
        RemainText = TotalFrames > done && rate > 0 ? "약 " + Clock((TotalFrames - done) / rate, words: true) : "곧 끝나요";
        if (preview is not null)
            ShowFrame(frame, preview);
    }

    private void ShowFrame(BoardRunFrame frame, BitmapSource preview)
    {
        VideoFrame = preview;
        var scale = frame.Info.OriginalWidth > 0 ? preview.PixelWidth / (double)frame.Info.OriginalWidth : 1;
        VideoBoxes = frame.Boxes.Select(b => new BoxOverlay(b.X1 * scale, b.Y1 * scale, b.X2 * scale, b.Y2 * scale,
            $"{NameOf("obj", b.ClassNumber)} {b.Score * 100:0}%", ColorOf(b.ClassNumber))).ToList();
        VideoTag = frame.Result.ClassId is { } cls ? NameOf("class", cls) : "";
        HudFrame = _video is { Frames: > 0 } v ? $"{frame.Info.Index + 1} / {v.Frames}" : $"{frame.Info.Index + 1}";
        HudVideoTime = $"NPU {frame.Result.NpuMs.ToString("0.0", CultureInfo.InvariantCulture)} ms";
        HudCount = frame.Result.ClassId is null ? $"검출 {frame.Boxes.Count}개" : "";
        PlayTimeText = _video is { Fps: > 0, Frames: > 0 } info ? $"{Clock(frame.Info.Seconds)} / {Clock(info.Frames / info.Fps)}" : Clock(frame.Info.Seconds);
    }

    partial void OnFrameIndexChanged(int value)
    {
        if (!IsVideoRunning)
            ShowVideoFrame(value);
    }

    /// <summary>After a run: the frame at <paramref name="index"/> with its boxes, on the nearest kept preview.</summary>
    private void ShowVideoFrame(int index)
    {
        BoardRunFrame? frame;
        BoardRunFrame? withPreview = null;
        lock (_videoFrames)
        {
            if (index < 0 || index >= _videoFrames.Count)
                return;
            frame = _videoFrames[index];
            for (var i = index; i >= 0 && withPreview is null; i--)
                if (_videoFrames[i].Preview.Length > 0)
                    withPreview = _videoFrames[i];
        }
        if (withPreview is null || Decode(withPreview.Preview) is not { } preview)
            return;
        ShowFrame(frame, preview);
    }

    private static BitmapSource? Decode(byte[] jpeg)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private bool CanBrowse() => IsVideoDone && !IsVideoRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayer();
            return;
        }
        if (FrameIndex >= FrameMax)
            FrameIndex = 0;
        var fps = _video is { Fps: > 0 } v ? v.Fps / _videoEvery : 10;
        _player = new DispatcherTimer(TimeSpan.FromMilliseconds(1000 / Math.Clamp(fps, 1, 30)), DispatcherPriority.Background, (_, _) =>
        {
            if (FrameIndex >= FrameMax)
                StopPlayer();
            else
                FrameIndex++;
        }, _ui);
        IsPlaying = true;
        _player.Start();
    }

    private void StopPlayer()
    {
        _player?.Stop();
        _player = null;
        IsPlaying = false;
    }

    /// <summary>"영상 저장": the video with the board's boxes drawn in (mp4) and the table (CSV), into a folder the user picks.</summary>
    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task SaveVideoResultAsync()
    {
        if (_services.EngineLayout is not { } layout || !HasVideo || (!SaveVideo && !SaveCsv))
            return;
        if (_services.Dialogs.PickFolder("결과를 저장할 폴더", ResultsFolder()) is not { } folder)
            return;
        List<BoardRunFrame> frames;
        lock (_videoFrames)
            frames = [.. _videoFrames];
        var stem = Path.GetFileNameWithoutExtension(VideoPath);
        var saved = new List<string>();
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "topst-board", "video")).FullName;
            var results = Path.Combine(work, stem + ".jsonl");
            await Task.Run(() => BoardVideoOutput.WriteResults(results, frames));
            if (SaveCsv)
            {
                var csv = Path.Combine(folder, stem + ".board.csv");
                await Task.Run(() => BoardVideoOutput.WriteCsv(csv, frames, cls => NameOfSafe("obj", cls)));
                saved.Add(Path.GetFileName(csv));
            }
            if (SaveVideo)
            {
                var font = BoardVideoOutput.KoreanFont() ?? throw new IOException("한글 글꼴(맑은 고딕)을 찾지 못했어요");
                var labels = frames.SelectMany(f => f.Boxes).Select(b => b.ClassNumber).Distinct().ToDictionary(c => c, c => NameOfSafe("obj", c));
                var output = Path.Combine(folder, stem + ".board.mp4");
                _shell.BeginTask("결과 영상 만드는 중", VideoTitle);
                await BoardVideoOutput.WriteVideoAsync(layout, VideoPath, results, labels, font, output,
                    (done, total) => _ui.BeginInvoke(() =>
                    {
                        _shell.TaskProgress = total > 0 ? done / (double)total : 0;
                        _shell.TaskDetail = total > 0 ? $"{done} / {total}장" : "";
                    }));
                saved.Add(Path.GetFileName(output));
                _shell.EndTask("결과 영상을 저장했어요", Path.GetFileName(output));
                _services.Dialogs.ShowInExplorer(output);
            }
            VideoSaveText = "저장했어요 · " + string.Join(" · ", saved);
        }
        catch (IOException ex)
        {
            Error = ex.Message;
            _shell.EndTask("결과 영상을 만들지 못했어요", "", StepState.Bad);
        }
    }

    // ---------------------------------------------------------------- names and colours
    private string NameOf(string type, int number) => BoardProjects.NameOf(_names, type, number);

    private string NameOfSafe(string type, int number) => NameOf(type, number);

    private Color ColorOf(int classNumber)
    {
        if (!_colors.TryGetValue(classNumber, out var color))
            _colors[classNumber] = color = Palette[_colors.Count % Palette.Length];
        return color;
    }

    private Brush Swatch(int classNumber)
    {
        var brush = new SolidColorBrush(ColorOf(classNumber));
        brush.Freeze();
        return brush;
    }
}
