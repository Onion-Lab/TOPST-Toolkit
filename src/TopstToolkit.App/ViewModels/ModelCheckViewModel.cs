using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.App.ViewModels;

/// <summary>
/// Tool "모델 점검": one model file in, one answer out — can the NPU run every layer. No project is made.
/// Layout-independent.
/// </summary>
public partial class ModelCheckViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private string? _checkedPath;

    public ModelCheckViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
    }

    public ObservableCollection<LayerProblem> Layers { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _isCompatible;
    [ObservableProperty] private bool _hasLayers;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _summaryTitle = "";
    [ObservableProperty] private string _summaryDetail = "";
    [ObservableProperty] private string _chipText = "";
    [ObservableProperty] private ChipKind _chipKind;
    [ObservableProperty] private string _error = "";

    public bool EngineMissing => _services.Engine is null;

    [RelayCommand]
    private async Task PickAsync()
    {
        if (_services.Dialogs.PickModelFile() is { } path)
            await CheckAsync(path);
    }

    public async Task CheckAsync(string path)
    {
        if (IsBusy || _services.Engine is null)
            return;
        if (_shell.IsBusy)
        {
            Error = "지금 하는 작업이 끝난 뒤에 점검할 수 있어요.";
            return;
        }
        if (ModelImporter.FormatOf(path) is null)
        {
            Error = "지원하지 않는 모델 형식이에요. .onnx, .tflite, .cfg, .enlight 파일을 골라 주세요.";
            return;
        }
        Error = "";
        HasResult = false;
        Layers.Clear();
        HasLayers = false;
        FileName = System.IO.Path.GetFileName(path);
        IsBusy = true;
        _shell.ClearLog();
        _shell.BeginTask("모델 점검 중", FileName);
        try
        {
            var checker = new ModelChecker(_services.Engine);
            var result = await Task.Run(() => checker.CheckAsync(path, e =>
            {
                if (e is TopstToolkit.Engine.Parsing.OutputLineEvent line && line.Text.Trim().Length > 0)
                    _shell.PostLog(line.Text.TrimEnd(), line.IsUpdate);
            }));
            Show(result);
            _checkedPath = path;
            _shell.EndTask("준비됨");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Error = ex.Message;
            _shell.EndTask("점검하지 못했어요", "", StepState.Bad);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(ModelCheckResult result)
    {
        HasResult = true;
        IsCompatible = result.IsCompatible;
        if (result.Error is { } error)
        {
            (ChipText, ChipKind) = ("확인 실패", ChipKind.Bad);
            SummaryTitle = error.Title;
            SummaryDetail = error.Detail;
            return;
        }
        var report = result.Compatibility;
        if (report is null)
        {
            (ChipText, ChipKind) = ("확인 실패", ChipKind.Bad);
            SummaryTitle = "호환성 표를 읽지 못했어요";
            SummaryDetail = "아래 로그를 열어 내용을 확인해 주세요.";
            return;
        }
        if (report.IsFullyCompatible)
        {
            (ChipText, ChipKind) = ("실행 가능", ChipKind.Ok);
            SummaryTitle = $"레이어 {report.Total}개 모두 NPU에서 실행됩니다";
            SummaryDetail = "이 모델로 프로젝트를 만들어 빌드하면 됩니다.";
            return;
        }
        (ChipText, ChipKind) = ("일부 불가", ChipKind.Warn);
        SummaryTitle = $"레이어 {report.Total}개 가운데 {report.Incompatible}개는 NPU가 실행하지 못해요";
        SummaryDetail = "박스 계산처럼 모델 끝에 붙은 부분이라면 그 앞에서 잘라내면 됩니다. 프로젝트를 만들어 빌드하면 잘라낼 곳을 고를 수 있어요.";
        foreach (var layer in report.Layers)
            Layers.Add(new LayerProblem(layer.Index, layer.Name, layer.Type, BuildViewModel.Explain(layer), layer.Description));
        HasLayers = Layers.Count > 0;
    }

    /// <summary>Continues with the checked model as a new project.</summary>
    [RelayCommand]
    private void CreateProject()
    {
        if (_checkedPath is not null && !_shell.StartNewProject(_checkedPath))
            Error = "지금 하는 작업이 끝난 뒤에 프로젝트를 만들 수 있어요.";
    }
}
