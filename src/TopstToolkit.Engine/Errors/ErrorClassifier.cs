using TopstToolkit.Engine.Parsing;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Engine.Errors;

/// <summary>
/// Known failure patterns → plain Korean messages (design §4.8, feature/04 §5.4, feature/09 §4).
/// Unknown failures become <see cref="ToolErrorCode.Unexpected"/> with the last meaningful line as evidence.
/// </summary>
public static class ErrorClassifier
{
    private sealed record Rule(ToolErrorCode Code, Func<string, bool> Match, Func<string, ToolKind, ToolError> Build, ToolKind[]? Tools = null);

    // Order matters: the more specific cause comes first ("'ascii' codec" is printed together with "Parser: error occurs").
    private static readonly Rule[] Rules =
    [
        new(ToolErrorCode.SdkVersionMismatch, l => l.Contains("Compiler does not support the", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.SdkVersionMismatch, "다른 버전의 툴킷으로 만든 .enlight 파일이에요",
                "원본 모델(ONNX 등)로 처음부터 다시 변환해 주세요.", SuggestedAction.ReconvertFromSource, l)),

        new(ToolErrorCode.NonAsciiPath, l => l.Contains("'ascii' codec can't", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NonAsciiPath, "경로나 파일 이름에 영문이 아닌 글자가 있어요",
                "툴킷은 모델 경로와 .enlight 파일 이름에 영문·숫자만 쓸 수 있어요. 작업 공간 위치와 모델 이름을 확인해 주세요.", SuggestedAction.CheckWorkspace, l)),

        new(ToolErrorCode.NonAsciiLabel, l => l.Contains("'latin-1' codec can't encode", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NonAsciiLabel, "라벨에 영문이 아닌 글자가 있어요",
                "툴킷에 넘기는 라벨은 영문만 쓸 수 있어요. 한글 이름은 표시용 라벨에 적어 주세요.", SuggestedAction.EditLabels, l)),

        new(ToolErrorCode.ModelConfigConflict, l => l.Contains("is invalid parameter at", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.ModelConfigConflict, "모델 설정 파일이 충돌했어요",
                "앱이 막아야 하는 상황이에요. 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics, l)),

        new(ToolErrorCode.NotCalibrated, l => l.Contains("No exist activation statistics data", StringComparison.Ordinal)
                                              || l.Contains("No exist activation histogram data", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NotCalibrated, "캘리브레이션이 되지 않은 모델이에요",
                "변환 단계에서 캘리브레이션 이미지를 넣고 다시 변환해 주세요.", SuggestedAction.GoToConvert, l)),

        new(ToolErrorCode.NotCalibrated, l => l.Contains("argument of type 'NoneType' is not iterable", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NotCalibrated, "캘리브레이션 없이 변환한 모델은 추론할 수 없어요",
                "변환 단계에서 캘리브레이션 이미지를 넣고 다시 변환해 주세요.", SuggestedAction.GoToConvert, l),
            [ToolKind.Simulator]),

        new(ToolErrorCode.CalibrationImagesNotFound, l => l.Contains("Please check whether dataset-root", StringComparison.Ordinal)
                                                         || l.Contains("Can't find any image", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.CalibrationImagesNotFound, "캘리브레이션 이미지를 찾지 못했어요",
                "이미지 폴더에 png·jpg·bmp 같은 이미지가 들어 있는지 확인해 주세요.", SuggestedAction.GoToData, l)),

        new(ToolErrorCode.DatasetNotFound, l => l.Contains("Dataset not found or corrupted", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.DatasetNotFound, "평가용 데이터셋을 찾지 못했어요",
                "데이터셋 폴더 구조를 확인해 주세요.", SuggestedAction.GoToData, l)),

        new(ToolErrorCode.NoDetectionLayer, l => l.Contains("No exist detection layer", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NoDetectionLayer, "검출 후처리가 없는 모델이에요",
                "검출 모델이라면 2 설정에서 박스 계산에 쓰는 파일(.bin 또는 .anchor)을 넣어 주세요.", SuggestedAction.GoToConvert, l)),

        new(ToolErrorCode.InvalidOptions, l => l.Contains("[Error] --type is mandatory", StringComparison.Ordinal),
            (l, _) => Options("모델 종류(분류·검출·기타)를 골라 주세요.", l)),
        new(ToolErrorCode.InvalidOptions, l => l.Contains("set num_class", StringComparison.Ordinal),
            (l, _) => Options("후처리를 붙이려면 클래스 수가 필요해요.", l)),
        new(ToolErrorCode.InvalidOptions, l => l.Contains("post process layer should exist", StringComparison.Ordinal),
            (l, _) => Options("검출 모델에는 박스 계산에 쓰는 파일(.bin 또는 .anchor)이 필요해요. 2 설정에서 골라 주세요. " +
                              "YOLOv8 원본 모델(.onnx)이라면 1 모델에서 다시 가져오면 앱이 자동으로 준비합니다.", l)),
        new(ToolErrorCode.InvalidOptions, l => l.Contains("[OptionErr]", StringComparison.Ordinal),
            (l, _) => Options("이 보드 설정에서는 쓸 수 없는 옵션이에요.", l)),

        new(ToolErrorCode.OutOfMemory, l => l.Contains("MemoryError", StringComparison.Ordinal)
                                           || l.Contains("Unable to allocate", StringComparison.Ordinal)
                                           || l.Contains("not enough memory", StringComparison.Ordinal)
                                           || l.Contains("DefaultCPUAllocator", StringComparison.Ordinal),
            (l, _) => OutOfMemory(l)),

        new(ToolErrorCode.EngineSetup, l => l.Contains("hw_config.py", StringComparison.Ordinal) && l.Contains("FileNotFoundError", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.EngineSetup, "작업 폴더가 준비되지 않았어요",
                "앱 내부 문제예요. 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics, l)),

        new(ToolErrorCode.InputMissing, l => l.EndsWith(" is not exist", StringComparison.Ordinal)
                                            || l.Contains("ENLIGHT_SIM: error occurs. Please check whether input", StringComparison.Ordinal)
                                            || l.Contains("No such file or directory", StringComparison.Ordinal)
                                            || (l.StartsWith("AssertionError:", StringComparison.Ordinal) && l.EndsWith("does not exist", StringComparison.Ordinal)),
            (l, _) => new(ToolErrorCode.InputMissing, "필요한 파일을 찾지 못했어요",
                "앞 단계가 끝까지 실행됐는지 확인하고 다시 빌드해 주세요.", SuggestedAction.None, l)),

        // What the NPU does not do, in the converter's and the compiler's words: printed together with "Parser: error
        // occurs" / "Compiler: error occurs", and much more useful than those (model zoo review, design §5.9).
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("not supported layer (", StringComparison.Ordinal),
            (l, _) => UnsupportedLayer(l)),
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("ReLU alpha(=negative slope) value only supports", StringComparison.Ordinal),
            (l, _) => Unsupported("LeakyReLU의 기울기가 0.1이 아니에요",
                "NPU의 LeakyReLU는 기울기 0.1만 돼요. 기울기 0.1로 학습한 모델을 쓰거나, 이 층 앞에서 모델을 잘라 주세요.", l)),
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("padL != padR", StringComparison.Ordinal),
            (l, _) => Unsupported("양쪽 패딩이 다른 층이 있어요",
                "NPU는 왼쪽과 오른쪽(위와 아래)에 같은 만큼만 덧댈 수 있어요. TensorFlow의 'SAME' 패딩에서 흔해요. " +
                "입력 크기를 홀수로(예: 224 → 225) 바꿔 내보내면 양쪽이 같아집니다.", l)),
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("Resize layer only supports a scale_factor of", StringComparison.Ordinal),
            (l, _) => Unsupported("2배가 아닌 크기 바꾸기(Resize)가 있어요",
                "NPU의 Resize는 가로세로 2배 키우기만 돼요. 이 층 앞에서 모델을 잘라 주세요.", l)),
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("flatten axis 1 only", StringComparison.Ordinal),
            (l, _) => Unsupported("Flatten이 1번 축이 아닌 곳에 있어요",
                "NPU의 Flatten은 채널부터 펼치는 꼴(축 1)만 돼요. 이 층 앞에서 모델을 잘라 주세요.", l)),
        new(ToolErrorCode.UnsupportedByNpu, l => l.Contains("LReLU, ReLU, Bypass only", StringComparison.Ordinal),
            (l, _) => Unsupported("합성곱 뒤의 활성화를 NPU가 처리하지 못해요",
                "합성곱 바로 뒤에는 ReLU · LeakyReLU만 붙일 수 있어요. hard-sigmoid · hard-swish는 " +
                "ReLU6 꼴(clip(x + 3, 0, 6) ÷ 6)로 바꿔 내보내면 됩니다.", l)),
        new(ToolErrorCode.NpuMemoryLimit, l => l.Contains("Not implemented partition for GlobalPooling", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NpuMemoryLimit, "전역 풀링이 NPU 메모리에 다 들어가지 않아요",
                "큰 특징 맵 위의 전역 평균 풀링(squeeze-excitation 등)은 나눠서 계산하지 못해요. " +
                "입력 크기를 줄여(예: 224 → 160) 다시 내보내 보세요.", SuggestedAction.None, l)),
        new(ToolErrorCode.NpuMemoryLimit, l => l.Contains("IACT Overflow", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.NpuMemoryLimit, "한 층의 입력이 NPU 메모리보다 커요",
                "입력 크기를 줄여(예: 224 → 160) 다시 내보내 보세요.", SuggestedAction.None, l)),

        new(ToolErrorCode.ModelLoadFailed, l => l.Contains("Parser: error occurs", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.ModelLoadFailed, "모델 파일을 읽지 못했어요",
                "ONNX·TFLite(FP32)·Darknet·.enlight 형식인지, 파일이 손상되지 않았는지 확인해 주세요.", SuggestedAction.None, l)),

        new(ToolErrorCode.CompilerFailed, l => l.Contains("Compiler: error occurs", StringComparison.Ordinal),
            (l, _) => new(ToolErrorCode.CompilerFailed, "컴파일 중 오류가 났어요",
                "compile_debug.log에 원인이 있어요. 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics, l)),
    ];

    private static ToolError Unsupported(string title, string detail, string evidence) =>
        new(ToolErrorCode.UnsupportedByNpu, title, detail, SuggestedAction.None, evidence);

    // Where the operators the converter rejects usually come from, so that the message says which part of the model it is.
    private static readonly Dictionary<string, string> OperatorOrigins = new(StringComparer.Ordinal)
    {
        ["Sqrt"] = "LayerNorm · GroupNorm 같은 정규화에서 나오는 연산이에요 (ConvNeXt · ViT 계열).",
        ["Erf"] = "GELU 활성화에서 나오는 연산이에요.",
        ["MatMul"] = "어텐션(Transformer) 블록의 행렬 곱이에요.",
        ["Einsum"] = "어텐션(Transformer) 블록의 행렬 곱이에요.",
        ["Mod"] = "Swin 같은 창(window) 어텐션의 위치 계산에서 나오는 연산이에요.",
        ["Range"] = "위치 계산에서 나오는 연산이에요.",
        ["CumSum"] = "위치 인코딩에서 나오는 연산이에요.",
        ["ReduceMax"] = "모델 안에 들어 있는 검출 후처리에서 나오는 연산이에요. 후처리 앞에서 잘라 주세요.",
        ["ReduceMin"] = "모델 안에 들어 있는 검출 후처리에서 나오는 연산이에요. 후처리 앞에서 잘라 주세요.",
        ["NonMaxSuppression"] = "모델 안에 들어 있는 검출 후처리에서 나오는 연산이에요. 후처리 앞에서 잘라 주세요.",
        ["TopK"] = "모델 안에 들어 있는 검출 후처리에서 나오는 연산이에요. 후처리 앞에서 잘라 주세요.",
        ["Expand"] = "ECA 같은 채널 주의(attention) 블록에서 나오는 연산이에요.",
        ["InstanceNormalization"] = "GroupNorm · InstanceNorm에서 나오는 연산이에요.",
    };

    private static ToolError UnsupportedLayer(string line)
    {
        var start = line.IndexOf("not supported layer (", StringComparison.Ordinal) + "not supported layer (".Length;
        var end = line.IndexOf(')', start);
        var op = end > start ? line[start..end] : "?";
        var origin = OperatorOrigins.TryGetValue(op, out var o) ? o + " " : "";
        return Unsupported($"NPU가 지원하지 않는 연산이 있어요 ({op})",
            origin + "이 연산이 없는 모델을 쓰거나, 이 연산 앞에서 모델을 잘라 주세요.", line);
    }

    private static ToolError Options(string detail, string evidence) =>
        new(ToolErrorCode.InvalidOptions, "설정이 빠졌거나 맞지 않아요", detail, SuggestedAction.GoToConvert, evidence);

    private static ToolError OutOfMemory(string? evidence) =>
        new(ToolErrorCode.OutOfMemory, "메모리가 부족해요",
            "다른 프로그램을 닫거나 캘리브레이션 이미지 수를 줄여서 다시 해 보세요.", SuggestedAction.CloseOtherApps, evidence);

    /// <summary>Classifies a finished run from its output alone. Returns null when nothing looks wrong in the output.</summary>
    public static ToolError? Classify(ToolRunResult run)
    {
        if (run.Cancelled)
            return new ToolError(ToolErrorCode.Cancelled, "실행을 취소했어요", "필요하면 다시 빌드해 주세요.", SuggestedAction.None);

        var lines = run.StderrLines.Concat(run.StdoutLines).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var rule in Rules)
        {
            if (rule.Tools is not null && !rule.Tools.Contains(run.Invocation.Tool))
                continue;
            var hit = lines.FirstOrDefault(rule.Match);
            if (hit is null)
                continue;
            var error = rule.Build(hit, run.Invocation.Tool);
            if (error.Code == ToolErrorCode.ModelLoadFailed)
            {
                var cause = CauseBefore(run.StderrLines, hit);
                // the parser prints nothing before "Parser: error occurs" when an operator handler is simply not
                // written (attention's MatMul in YOLO11 · YOLOv10 · YOLO26); its debug log names the handler
                if (cause is null && ParserTraceOperator(run.WorkingDirectory) is { } op)
                    return UnsupportedLayer($"not supported layer ({op})") with { Evidence = $"parser_debug.log: handle_{op.ToLowerInvariant()} NotImplementedError" };
                error = error with { Evidence = cause ?? hit };
            }
            return error;
        }

        if (run.ExitCode != 0)
        {
            // NTSTATUS codes seen when Windows itself ends the process for lack of memory.
            if (run.ExitCode is unchecked((int)0xC0000017) or unchecked((int)0xC000012D))
                return OutOfMemory($"exit code 0x{run.ExitCode:X8}");

            var last = LastMeaningful(run.StderrLines) ?? LastMeaningful(run.StdoutLines);
            return new ToolError(ToolErrorCode.Unexpected, "예상하지 못한 오류가 났어요",
                $"종료 코드 {run.ExitCode}. 로그를 확인하거나 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics, last);
        }
        return null;
    }

    /// <summary>The converter keeps going when layers are incompatible (exit code 0); this turns the report into an error.</summary>
    public static ToolError? FromCompatibility(CompatibilityReport? report)
    {
        if (report is null)
            return new ToolError(ToolErrorCode.CompatibilityUnknown, "호환성 검사 결과를 읽지 못했어요",
                "변환 로그를 확인하거나 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics);
        if (report.IsFullyCompatible)
            return null;
        var names = string.Join(", ", report.Layers.Take(3).Select(l => $"{l.Type}({l.Name})"));
        if (report.Layers.Count > 3)
            names += " 외";
        return new ToolError(ToolErrorCode.IncompatibleLayers, $"NPU가 지원하지 않는 레이어가 {report.Incompatible}개 있어요",
            $"{names}. 이 레이어 앞에서 모델을 잘라내면 변환할 수 있어요.", SuggestedAction.ShowCompatibility);
    }

    public static ToolError MissingDoneMarker(ToolRunResult run) =>
        new(ToolErrorCode.MissingDoneMarker, "도구가 끝까지 실행되지 않았어요",
            "로그를 확인하거나 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics,
            LastMeaningful(run.StderrLines) ?? LastMeaningful(run.StdoutLines));

    public static ToolError MissingOutputs(IEnumerable<string> missing) =>
        new(ToolErrorCode.MissingOutputs, "결과 파일이 만들어지지 않았어요",
            "빠진 파일: " + string.Join(", ", missing.Select(Path.GetFileName)), SuggestedAction.MakeDiagnostics);

    public static ToolError ToolchainMissing() =>
        new(ToolErrorCode.ToolchainMissing, "보드용 라이브러리 빌드 도구가 설치되어 있지 않아요",
            "설치할 때 \"보드용 라이브러리 빌드 도구\"를 선택하지 않았다면 지금 내려받을 수 있어요 (약 340 MB).", SuggestedAction.InstallBoardTools);

    public static ToolError BoardBuildFailed(string? evidence) =>
        new(ToolErrorCode.BoardBuildFailed, "보드용 라이브러리(net.so)를 만들지 못했어요",
            "컴파일 로그를 확인하거나 진단 패키지를 만들어 알려 주세요.", SuggestedAction.MakeDiagnostics, evidence);

    private static readonly Dictionary<string, string> HandlerOperators = new(StringComparer.Ordinal)
    {
        ["matmul"] = "MatMul", ["einsum"] = "Einsum", ["gemm"] = "Gemm", ["layernorm"] = "LayerNormalization",
    };

    /// <summary>
    /// The operator whose handler in the parser is not written: the last <c>handle_&lt;op&gt;</c> frame of a
    /// NotImplementedError in <c>parser_debug.log</c> (written into the step's working folder). Null when there is none.
    /// </summary>
    private static string? ParserTraceOperator(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory))
            return null;
        var log = Path.Combine(workingDirectory, "parser_debug.log");
        try
        {
            if (!File.Exists(log) || new FileInfo(log).Length > 1 << 20)
                return null;
            var lines = File.ReadAllLines(log);
            if (!lines.Any(l => l.Trim() == "NotImplementedError"))
                return null;
            string? handler = null;
            foreach (var line in lines)
            {
                var at = line.IndexOf(", in handle_", StringComparison.Ordinal);
                if (at >= 0)
                    handler = line[(at + ", in handle_".Length)..].Trim();
            }
            if (string.IsNullOrEmpty(handler) || !handler.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return null;
            return HandlerOperators.TryGetValue(handler, out var op) ? op : char.ToUpperInvariant(handler[0]) + handler[1..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The toolkit prints the real cause on the line before "Parser: error occurs." (feature/04 §5.4).</summary>
    private static string? CauseBefore(IReadOnlyList<string> lines, string hit)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() != hit) continue;
            for (var j = i - 1; j >= 0; j--)
            {
                var t = lines[j].Trim();
                if (t.Length > 0) return t;
            }
        }
        return null;
    }

    private static string? LastMeaningful(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.Length > 0 && !t.StartsWith("<frozen", StringComparison.Ordinal) && !t.Contains("UserWarning", StringComparison.Ordinal))
                return t;
        }
        return null;
    }
}
