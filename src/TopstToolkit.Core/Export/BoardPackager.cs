using System.Globalization;
using System.IO.Compression;
using System.Text;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Export;

/// <param name="Destination">Folder the package folder is created in. May contain Korean or spaces: the toolkit never reads it.</param>
/// <param name="IncludeRuntimeSource">Add the C runtime sources so <c>net.so</c> can be rebuilt by hand.</param>
/// <param name="Zip">Also write <c>&lt;package&gt;.zip</c> next to the folder.</param>
public sealed record ExportOptions(string Destination, bool IncludeRuntimeSource = true, bool IncludeProjectFile = true, bool Zip = false);

/// <param name="Root">The package folder.</param>
/// <param name="BoardFolder">The folder inside it that is copied to the board (<c>&lt;name&gt;_quantized</c>).</param>
public sealed record ExportResult(string Root, string BoardFolder, string ReadmePath, int FileCount, long TotalBytes,
    bool HasNetSo, bool HasSampleInput, string? ZipPath);

/// <summary>
/// Step 7 "내보내기": lays the build outputs out the way the official TOPST procedure expects (design §5.6), so the
/// user copies one folder to the board, and writes a README with the commands and the numbers fixed at build time.
/// </summary>
public sealed class BoardPackager(ProjectStore store, EngineLayout? layout)
{
    /// <summary>Marks a folder this app created, so exporting again may replace it. Other folders are never deleted.</summary>
    public const string MarkerFile = ".topst-toolkit-export";

    /// <summary>Why the project cannot be exported yet, in a plain sentence; null when it can.</summary>
    public string? Problem(ProjectFile project)
    {
        if (BuildPaths.For(store, project) is not { } paths || !project.Headline.IsBuilt)
            return "아직 빌드하지 않았어요. 4 빌드를 먼저 끝내 주세요.";
        var missing = CompilerOutputs.FindMissing(paths.CodeDir);
        if (missing.Count > 0)
            return "빌드 결과 파일이 없어요. 4 빌드를 다시 실행해 주세요.";
        return null;
    }

    public ExportResult Export(ProjectFile project, ExportOptions options, DateTimeOffset? now = null)
    {
        if (Problem(project) is { } problem)
            throw new InvalidOperationException(problem);
        var time = now ?? DateTimeOffset.Now;
        var paths = BuildPaths.For(store, project)!;
        var folders = store.FoldersOf(project);

        var root = PrepareRoot(options.Destination, SafeFolderName(project.Name, paths.Stem));
        var board = Directory.CreateDirectory(Path.Combine(root, Path.GetFileName(paths.CodeDir))).FullName;

        // everything the compiler wrote (decision D5 "전부 포함"), plus net.so when it was built
        foreach (var file in Directory.EnumerateFiles(paths.CodeDir))
            File.Copy(file, Path.Combine(board, Path.GetFileName(file)), overwrite: true);
        var hasNetSo = File.Exists(Path.Combine(board, "net.so"));

        // NPU input of the first check picture: tcnputestapp reads sample/input.ia.bin
        var hasSample = false;
        if (Directory.Exists(paths.Int8DumpDir))
        {
            var dump = Directory.EnumerateDirectories(paths.Int8DumpDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "input.ia.bin")).FirstOrDefault(File.Exists);
            if (dump is not null)
            {
                File.Copy(dump, Path.Combine(Directory.CreateDirectory(Path.Combine(board, "sample")).FullName, "input.ia.bin"), overwrite: true);
                hasSample = true;
            }
        }

        var labels = LabelsOf(project);
        if (labels is not null)
            File.Copy(labels, Path.Combine(root, "labels.txt"), overwrite: true);

        // the app's fast post-processing, when net.so was built with it: its source next to the toolkit's, how to
        // build it by hand, and the script that compares the two on a board
        var kind = hasNetSo ? ProjectBuilder.BoardPostProcessOf(project) : "toolkit";
        var fastSource = layout is not null && FastPostProcess.SourceOfKind(kind) is { } fastName
            && File.Exists(Path.Combine(layout.BoardSourceDir, fastName)) ? Path.Combine(layout.BoardSourceDir, fastName) : null;
        // or the code the app generated for the part of the model behind its cut (design §5.8, stage 나), kept by the project
        var tailSource = kind == TailCode.Kind && Path.Combine(folders.TailDir, TailCode.SourceName) is var generated && File.Exists(generated) ? generated : null;
        var tail = tailSource is null ? null : TailFiles.LoadGeneration(folders.TailDir);
        var ownSource = fastSource ?? tailSource;

        if (options.IncludeRuntimeSource && layout is not null && Directory.Exists(layout.BoardRuntimeSourceDir))
        {
            var runtime = Path.Combine(root, "runtime");
            CopyTree(layout.BoardRuntimeSourceDir, runtime);
            if (ownSource is not null)
            {
                File.Copy(ownSource, Path.Combine(runtime, Path.GetFileName(ownSource)), overwrite: true);
                File.WriteAllText(Path.Combine(runtime, FastBuildScript), BuildFastScript(Path.GetFileName(ownSource)), new UTF8Encoding(false));
            }
        }
        var hasBoardCheck = false;
        if (fastSource is not null && Path.Combine(layout!.BoardSourceDir, BoardCheckScript) is var check && File.Exists(check))
        {
            File.Copy(check, Path.Combine(Directory.CreateDirectory(Path.Combine(root, "board-check")).FullName, BoardCheckScript), overwrite: true);
            hasBoardCheck = true;
        }
        var hasTailCheck = tail is { Ok: true } && layout is not null && PackTailCheck(tail, folders, paths, layout, root);

        if (options.IncludeProjectFile)
            File.Copy(Path.Combine(folders.Root, ProjectStore.FileName), Path.Combine(root, ProjectStore.FileName), overwrite: true);

        var readme = Path.Combine(root, "README.md");
        File.WriteAllText(readme, BuildReadme(project, paths, hasNetSo, hasSample, labels, time,
            fastSource is null ? null : Path.GetFileName(fastSource), hasBoardCheck, options.IncludeRuntimeSource, tail is { Ok: true } ? tail : null, hasTailCheck),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, MarkerFile), "TOPST Toolkit export\n", new UTF8Encoding(false));

        string? zip = null;
        if (options.Zip)
        {
            zip = root + ".zip";
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(root, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        project.Headline.ExportedAt = time;
        store.Save(project, time);
        return new ExportResult(root, board, readme, files.Count, files.Sum(f => f.Length), hasNetSo, hasSample, zip);
    }

    /// <summary>The toolkit label file the project was built with, or null (no labels).</summary>
    public string? LabelsOf(ProjectFile project)
    {
        if (project.Options.Labels.Toolkit is { } custom)
            return store.ToAbsolute(project, custom) is var p && File.Exists(p) ? p : null;
        if (layout is null || ProjectBuilder.LabelFileName(project.Options.Labels.Set) is not { } name)
            return null;
        var file = Path.Combine(layout.LabelsDir, name);
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// <c>board-check\</c> for generated tail code: the script, the NPU outputs the code reads for the first check picture
    /// (<c>dump\</c>) and what the PC computed from them (<c>expect\</c>). False when one of them is missing.
    /// </summary>
    private static bool PackTailCheck(TailGeneration tail, ProjectWorkspace folders, BuildPaths paths, EngineLayout layout, string root)
    {
        var script = Path.Combine(layout.BoardSourceDir, TailCode.BoardCheckScript);
        if (!File.Exists(script) || TailFiles.LoadEvaluation(folders.TailDir)?.Runs.FirstOrDefault() is not { } run)
            return false;
        var picture = Path.GetFileName(Path.TrimEndingDirectorySeparator(run.Dump));
        var dump = Path.Combine(paths.Int8DumpDir, picture);
        if (!Directory.Exists(dump))
            return false;
        var dumpFiles = tail.Inputs.Select(i => i.Npu?.Name).OfType<string>()
            .Select(name => Directory.EnumerateFiles(dump, name + "___*.oa.bin").Concat(Directory.EnumerateFiles(dump, name + ".oa.bin")).FirstOrDefault())
            .ToList();
        var pc = TailFiles.PcFolder(folders.TailDir, picture);
        var expectFiles = run.Outputs.Select(o => Path.Combine(pc, o.Index.ToString(CultureInfo.InvariantCulture) + ".bin")).ToList();
        if (dumpFiles.Count == 0 || dumpFiles.Any(f => f is null) || expectFiles.Count == 0 || expectFiles.Any(f => !File.Exists(f)))
            return false;
        var check = Directory.CreateDirectory(Path.Combine(root, "board-check")).FullName;
        File.Copy(script, Path.Combine(check, TailCode.BoardCheckScript), overwrite: true);
        var dumpTo = Directory.CreateDirectory(Path.Combine(check, "dump")).FullName;
        foreach (var file in dumpFiles)
            File.Copy(file!, Path.Combine(dumpTo, Path.GetFileName(file!)), overwrite: true);
        var expectTo = Directory.CreateDirectory(Path.Combine(check, "expect")).FullName;
        foreach (var file in expectFiles)
            File.Copy(file, Path.Combine(expectTo, Path.GetFileName(file)), overwrite: true);
        return true;
    }

    /// <summary>Written into <c>runtime\</c> next to the app's own source: the three gcc calls the app made (Linux shell).</summary>
    public const string FastBuildScript = "build_fast.sh";

    /// <summary>Copied into <c>board-check\</c>: compares the fast and the toolkit post-processing on a board.</summary>
    public const string BoardCheckScript = "check_post_process.py";

    /// <summary>
    /// The shell script that builds <c>net.so</c> with a fast source the way the app does, made from the same flag
    /// lists (<see cref="NetSoBuild"/>) so that it cannot drift from what the app runs.
    /// </summary>
    public static string BuildFastScript(string fastSource)
    {
        static string Line(IEnumerable<string> words) => string.Join(' ', words);
        var b = new StringBuilder();
        b.Append("#!/bin/sh\n");
        b.Append("# Builds net.so with TOPST Toolkit's own post-processing source (" + fastSource + "), the way the app does.\n");
        b.Append("# Run in this folder after copying network.h and post_process.c of the compiled network into it.\n");
        b.Append("# Needs the Arm GNU-A 9.2-2019.12 toolchain (aarch64-none-linux-gnu) on PATH, or CROSS_COMPILE set.\n");
        b.Append("# Plain \"make\" still builds the official library, with the toolkit's own post-processing only.\n");
        b.Append("set -e\n");
        b.Append("CC=${CROSS_COMPILE:-aarch64-none-linux-gnu-}gcc\n");
        b.Append("$CC " + Line(NetSoBuild.CompileFlags) + " -c " + Line(NetSoBuild.SourcesFor(fastSource)) + "\n");
        b.Append("$CC " + Line(NetSoBuild.FastCompileFlags) + " -c " + fastSource + "\n");
        b.Append("$CC -shared -Wl,-soname," + NetSoBuild.OutputName + " -o " + NetSoBuild.OutputName + " " + Line(NetSoBuild.ObjectsFor(fastSource)) + "\n");
        b.Append("rm -f ./*.o\n");
        return b.ToString();
    }

    /// <summary>The README of the package: how to copy and run it on the board and what was fixed at build time.</summary>
    /// <param name="fastSource">File name of the fast post-processing source <c>net.so</c> was built with, or null.</param>
    /// <param name="tail">What was generated for the part behind the cut when <c>net.so</c> was built with it, or null.</param>
    /// <param name="hasTailCheck">The package holds <c>board-check\</c> for the generated tail code.</param>
    public string BuildReadme(ProjectFile project, BuildPaths paths, bool hasNetSo, bool hasSample, string? labelsFile, DateTimeOffset now,
        string? fastSource = null, bool hasBoardCheck = false, bool hasRuntimeSource = true, TailGeneration? tail = null, bool hasTailCheck = false)
    {
        var inv = CultureInfo.InvariantCulture;
        var boardName = Path.GetFileName(paths.CodeDir);
        var c = project.Options.Convert;
        var header = NetworkHeaderSummary.ParseFile(Path.Combine(paths.CodeDir, "network.h"));
        var buffers = NetworkBuffers.ParseFile(Path.Combine(paths.CodeDir, "network_buf.txt"));
        var perf = PerfResult.ParseFile(paths.PerfJson);
        var preset = PresetCatalog.Find(project.Preset);
        var isDetection = project.Task == "obj";
        var b = new StringBuilder();

        b.AppendLine($"# {project.Name} — TOPST AI-G 보드 배포 패키지");
        b.AppendLine();
        b.AppendLine($"TOPST Toolkit {project.Versions.App}으로 {now.ToLocalTime():yyyy-MM-dd HH:mm}에 만들었습니다. (ENLIGHT SDK {project.Versions.Sdk})");
        b.AppendLine();
        b.AppendLine("## 보드에서 실행하기");
        b.AppendLine();
        b.AppendLine($"1. `{boardName}` 폴더를 통째로 보드에 복사합니다.");
        b.AppendLine();
        b.AppendLine("   ```");
        b.AppendLine($"   scp -r {boardName}/ root@<보드 IP>:/home/root");
        b.AppendLine("   ```");
        b.AppendLine();
        b.AppendLine("2. 보드에서 실행합니다.");
        b.AppendLine();
        b.AppendLine("   ```");
        b.AppendLine($"   tcnnapp -n {boardName}/");
        b.AppendLine("   ```");
        b.AppendLine();
        b.AppendLine($"   추론 시간만 재려면 `tcnputestapp -n {boardName}/` 를 씁니다." +
                     (hasSample ? " 이 패키지의 `sample/input.ia.bin`(결과 확인용 사진)을 입력으로 씁니다." : ""));
        b.AppendLine();
        // verified on a TOPST AI-G board on 2026-11-22 (design §5.8): file in, file out, no camera and no display
        b.AppendLine("   카메라와 화면 없이 사진 한 장으로 확인할 수도 있습니다. 입력과 출력은 PNG 파일이고, 추론을 한 번 한 뒤 메뉴에서 `x`를 누르면 끝납니다. 결과 그림에 상자와 추론 시간이 그려집니다.");
        b.AppendLine();
        b.AppendLine("   ```");
        b.AppendLine($"   tcnnapp -n {boardName}/ -i file -p 사진.png -o file -P 결과.png");
        b.AppendLine("   ```");
        b.AppendLine();
        b.AppendLine($"   `tcnnapp`이 실제로 읽는 파일은 `{boardName}/`의 `net.so`, `npu_cmd.bin`, `quantized_network.bin` 세 개입니다 (`tcnputestapp`은 `sample/input.ia.bin`도 씁니다). 보드의 저장 공간이 모자라면 이것들만 복사해도 됩니다.");
        b.AppendLine();
        if (!hasNetSo)
        {
            b.AppendLine("> **`net.so`가 들어 있지 않습니다.** 이 PC에 보드 도구(Arm 크로스 컴파일러)가 없어서 만들지 못했습니다. 보드에서 모델을 쓰려면 `net.so`가 필요합니다.");
            b.AppendLine("> TOPST Toolkit의 설정 › 구성 요소에서 보드 도구를 설치한 뒤 다시 빌드하거나, Linux에서 직접 만듭니다:");
            b.AppendLine(">");
            b.AppendLine($"> 1. `{boardName}/network.h`와 `{boardName}/post_process.c`를 `runtime/`에 복사");
            b.AppendLine("> 2. Arm GNU-A 9.2-2019.12 (`aarch64-none-linux-gnu`) 툴체인으로 `runtime/`에서 `make`");
            b.AppendLine($"> 3. 만들어진 `net.so`를 `{boardName}/`에 복사");
            b.AppendLine();
        }

        b.AppendLine("## 모델");
        b.AppendLine();
        b.AppendLine("| 항목 | 값 |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| 원본 모델 | `{Path.GetFileName(project.Model!.Source)}` (SHA-256 `{project.Model.Sha256}`) |");
        b.AppendLine($"| 종류 | {PresetCatalog.DescribeKind(project.Task, project.Preset)}{(preset is null ? "" : $" — 프리셋 {preset.DisplayName}")} |");
        if (header?.Input is { } input)
            b.AppendLine($"| NPU 입력 | {string.Join(" × ", input.Shape)} ({input.DataType}, scale {input.Scale.ToString("R", inv)}) |");
        if (project.Options.Calibration.Source != "npy")
        {
            b.AppendLine($"| 이미지 전처리 | (픽셀 ÷ 255 − mean) ÷ std · mean {Nums(c.Mean)} · std {Nums(c.Std)} · RGB |");
            b.AppendLine($"| 비율 유지 (letterbox) | {(c.Letterbox ? "켬 — 사진을 늘이지 않고 여백을 채워 입력 크기에 맞춥니다" : "끔 — 사진을 입력 크기로 늘입니다")} |");
        }
        if (isDetection)
        {
            b.AppendLine($"| 후처리 | {preset?.Family ?? "검출"}{(c.PostProcess is { } pp ? $" · `{Path.GetFileName(pp)}`" : "")} — `net.so` 안에서 박스를 계산합니다" +
                         (fastSource is null ? "" : " (TOPST Toolkit의 빠른 코드, 아래 \"박스 계산 코드\")") + " |");
            b.AppendLine($"| 검출 최소 점수 (th-conf) | {project.Options.Compile.ThConf.ToString("0.###", inv)} — **보드용 코드에 고정된 값** |");
            b.AppendLine($"| 겹침 제거 기준 (th-iou) | {project.Options.Compile.ThIou.ToString("0.###", inv)} — **보드용 코드에 고정된 값** |");
        }
        if (tail is not null)
            b.AppendLine($"| 후처리 | 잘라낸 뒤쪽 구간(연산 {tail.Ops}개)을 `net.so`의 C 코드가 계산합니다 — 아래 \"잘라낸 구간의 코드\" |");
        if (c.NumClass is { } classes && project.Task != "unknown")
            b.AppendLine($"| 클래스 | {classes}개{(labelsFile is null ? "" : " — `labels.txt` (한 줄에 하나, 줄 번호 − 1 = 클래스 번호)")} |");
        if (project.Headline is { CompatibleLayers: { } ok, TotalLayers: { } total })
            b.AppendLine($"| NPU 호환 레이어 | {ok} / {total} |");
        b.AppendLine($"| 캘리브레이션 | {CalibrationText(project)} |");
        b.AppendLine();

        if (fastSource is not null)
        {
            b.AppendLine("## 박스 계산 코드");
            b.AppendLine();
            // what is different and what was measured (TOPST AI-G, one Cortex-A53 core, 2026-11-22: design §5.8)
            var (how, measured) = fastSource == FastPostProcess.SsdSource
                ? ("상자를 가려내는 방법만 다릅니다: 모든 상자의 점수를 하나하나 실수로 바꿔 계산하는 대신, NPU 출력의 int8 값을 NEON으로 16개씩 보고 물체일 수 있는 상자만 먼저 고른 뒤 그 상자들만 계산합니다.",
                   FastPostProcess.SsdMeasured)
                : ("후보를 찾는 방법만 다릅니다: 값마다 표를 거치지 않고 NPU 출력의 int8 값을 NEON으로 16개씩 직접 비교합니다.",
                   FastPostProcess.YoloMeasured);
            b.AppendLine($"`net.so`에는 TOPST Toolkit이 넣은 **빠른 박스 계산 코드**(`{fastSource}`)가 들어 있습니다. 툴킷의 원래 코드와 **같은 검출 결과**를 내고, " +
                         how + " " + measured);
            b.AppendLine();
            b.AppendLine("툴킷의 원래 코드도 같은 `net.so` 안에 그대로 있습니다. 보드에서 환경 변수로 고릅니다.");
            b.AppendLine();
            b.AppendLine("| 실행 | 뜻 |");
            b.AppendLine("|---|---|");
            b.AppendLine($"| `tcnnapp -n {boardName}/` | 빠른 코드 (기본). 툴킷 코드와 같은 줄을 출력합니다 |");
            b.AppendLine($"| `TOPST_POST_PROCESS=quiet tcnnapp -n {boardName}/` | 빠른 코드, 프레임마다 검출 결과를 한 줄씩 찍지 않음 — 출력이 필요 없을 때 가장 빠릅니다 |");
            b.AppendLine($"| `TOPST_POST_PROCESS=reference tcnnapp -n {boardName}/` | 툴킷의 원래 코드 (시간 · 결과를 비교할 때) |");
            b.AppendLine();
            b.AppendLine("빠른 코드가 다루지 않는 구성(예: 최대 검출 수 지정, 보드에서 sigmoid를 이미 적용한 출력)이면 자동으로 원래 코드가 실행됩니다.");
            b.AppendLine();
            if (hasBoardCheck)
            {
                b.AppendLine("두 코드의 결과가 같은지 보드에서 직접 확인할 수 있습니다. NPU · 카메라 · 화면은 쓰지 않고, 임의로 만든 출력값으로 두 코드를 번갈아 불러 비교합니다 (1~2분).");
                b.AppendLine();
                b.AppendLine("```");
                b.AppendLine($"scp board-check/{BoardCheckScript} root@<보드 IP>:/tmp/");
                b.AppendLine($"ssh root@<보드 IP> \"python3 /tmp/{BoardCheckScript} --kind {FastPostProcess.BoardCheckKind(FastPostProcess.KindOf(fastSource))} --net /home/root/{boardName}/net.so\"");
                b.AppendLine("```");
                b.AppendLine();
            }
            if (hasRuntimeSource)
            {
                b.AppendLine($"직접 다시 빌드할 때: `runtime/`에 `{boardName}/network.h`와 `{boardName}/post_process.c`를 복사한 뒤 `sh {FastBuildScript}` (빠른 코드 포함) 또는 `make` (툴킷 코드만).");
                b.AppendLine();
            }
        }

        if (tail is not null)
        {
            // what the generated code is, how a program reads its outputs, and how to check it on a board (design §5.8, stage 나)
            b.AppendLine("## 잘라낸 구간의 코드");
            b.AppendLine();
            b.AppendLine($"이 모델은 NPU가 실행하지 못하는 뒤쪽 부분을 잘라내고 빌드했습니다 (자른 곳: {string.Join(", ", tail.Inputs.Select(i => $"`{i.Cut}`"))}). " +
                         $"잘라낸 연산 {tail.Ops}개({string.Join(", ", tail.OpTypes)})는 TOPST Toolkit이 C 코드(`{TailCode.SourceName}`)로 옮겨 `net.so`에 넣었습니다. " +
                         "`net.so`가 NPU 출력에서 모델의 원래 출력을 계산합니다.");
            b.AppendLine();
            b.AppendLine("| 출력 | 모양 |");
            b.AppendLine("|---|---|");
            foreach (var o in tail.Outputs)
                b.AppendLine($"| `{o.Name}` | {string.Join(" × ", o.Shape)} |");
            b.AppendLine();
            b.AppendLine("NPU 출력은 8비트라서 값마다 256가지 가운데 하나입니다. 그래서 sigmoid · exp · 상수 곱 같은 계산은 256칸 표 하나로 줄였고, " +
                         "softmax와 그 뒤의 1×1 합성곱(YOLOv8의 DFL 같은)은 한 번에 계산합니다. " + TailCode.Measured);
            b.AppendLine();
            b.AppendLine("보드의 프로그램에서는 `net.so`의 다음 함수로 값을 읽습니다. `run_post_process()`를 부른 뒤부터 다음 호출 전까지 유효합니다.");
            b.AppendLine();
            b.AppendLine("```c");
            b.AppendLine("int topst_tail_output_count(void);");
            b.AppendLine("const float *topst_tail_output(int index, const char **name, int *rank, const int **dims);");
            b.AppendLine("```");
            b.AppendLine();
            b.AppendLine("종류가 \"기타\"인 모델이라 `tcnnapp`은 상자를 그리지 않습니다. 툴킷의 원래 코드(참고용)도 같은 `net.so` 안에 있고, 보드에서 환경 변수로 고릅니다.");
            b.AppendLine();
            b.AppendLine("| 실행 | 뜻 |");
            b.AppendLine("|---|---|");
            b.AppendLine($"| `tcnnapp -n {boardName}/` | 잘라낸 구간 코드 (기본). 프레임마다 출력의 처음 값 몇 개를 한 줄 찍습니다 |");
            b.AppendLine($"| `TOPST_POST_PROCESS=quiet tcnnapp -n {boardName}/` | 같은 계산, 찍지 않음 |");
            b.AppendLine($"| `TOPST_POST_PROCESS=reference tcnnapp -n {boardName}/` | 툴킷의 원래 코드 (종류 \"기타\"에 툴킷이 넣는 참고 코드) |");
            b.AppendLine();
            if (hasTailCheck)
            {
                b.AppendLine("보드에서 맞게 계산하는지 직접 확인할 수 있습니다. 결과 확인용 사진의 NPU 출력(`board-check/dump/`)으로 `net.so`를 부르고, " +
                             "PC가 같은 출력에서 계산한 값(`board-check/expect/`)과 비교합니다. NPU · 카메라 · 화면은 쓰지 않습니다.");
                b.AppendLine();
                b.AppendLine("```");
                b.AppendLine("scp -r board-check root@<보드 IP>:/tmp/");
                b.AppendLine($"ssh root@<보드 IP> \"cd /tmp/board-check && python3 {TailCode.BoardCheckScript} --net /home/root/{boardName}/net.so --dump dump --expect expect\"");
                b.AppendLine("```");
                b.AppendLine();
            }
            if (hasRuntimeSource)
            {
                b.AppendLine($"직접 다시 빌드할 때: `runtime/`에 `{boardName}/network.h`와 `{boardName}/post_process.c`를 복사한 뒤 `sh {FastBuildScript}` (잘라낸 구간 코드 포함) 또는 `make` (툴킷 코드만).");
                b.AppendLine();
            }
        }

        if (buffers is not null)
        {
            b.AppendLine("## 보드에서 쓰는 메모리");
            b.AppendLine();
            b.AppendLine("| 버퍼 | 크기 |");
            b.AppendLine("|---|---|");
            b.AppendLine($"| NPU 명령 (CODE) | {Size(buffers.Code)} |");
            b.AppendLine($"| 가중치 (WGHT) | {Size(buffers.Weight)} |");
            b.AppendLine($"| 작업 버퍼 (WORK) | {Size(buffers.Work)} |");
            b.AppendLine($"| 입력 (IACT) | {Size(buffers.InputActivation)} |");
            b.AppendLine($"| 출력 (OACT) | {Size(buffers.OutputActivation)} |");
            b.AppendLine($"| 합계 | **{Size(buffers.Total)}** |");
            b.AppendLine();
        }

        if (perf is not null)
        {
            b.AppendLine("## 추정 추론 시간");
            b.AppendLine();
            b.AppendLine($"**{perf.InferenceTimeMs.ToString("0.0##", inv)} ms** (약 {(1000 / perf.InferenceTimeMs).ToString("0", inv)} FPS) — NPU 연산 시간만, 툴킷 예제 클럭 설정 기준입니다. **보드에서 잰 값이 아닙니다.** 실제 값은 `tcnputestapp`으로 확인하세요.");
            b.AppendLine();
        }

        b.AppendLine("## 들어 있는 것");
        b.AppendLine();
        b.AppendLine("| 경로 | 내용 |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| `{boardName}/` | **보드로 복사하는 폴더.** `npu_cmd.bin`(NPU 명령), `quantized_network.bin`(가중치), `net.so`(후처리 라이브러리)와 컴파일러가 만든 나머지 파일 |");
        if (hasSample)
            b.AppendLine($"| `{boardName}/sample/input.ia.bin` | 결과 확인용 사진을 NPU 입력 형식으로 바꾼 것 |");
        if (labelsFile is not null)
            b.AppendLine("| `labels.txt` | 클래스 이름 |");
        b.AppendLine("| `runtime/` | `net.so`의 C 소스 (직접 다시 빌드할 때) |");
        if (fastSource is not null && hasRuntimeSource)
            b.AppendLine($"| `runtime/{fastSource}`, `runtime/{FastBuildScript}` | 빠른 박스 계산 코드와 그것을 넣어 빌드하는 명령 |");
        if (hasBoardCheck)
            b.AppendLine($"| `board-check/{BoardCheckScript}` | 보드에서 빠른 코드와 툴킷 코드의 결과 · 시간을 비교하는 스크립트 |");
        if (tail is not null && hasRuntimeSource)
            b.AppendLine($"| `runtime/{TailCode.SourceName}`, `runtime/{FastBuildScript}` | 잘라낸 구간 코드와 그것을 넣어 빌드하는 명령 |");
        if (hasTailCheck)
            b.AppendLine($"| `board-check/` | 보드에서 잘라낸 구간 코드의 결과를 PC 계산과 비교하는 스크립트(`{TailCode.BoardCheckScript}`), 그 입력(`dump/`)과 기댓값(`expect/`) |");
        b.AppendLine("| `project.json` | 이 결과를 다시 만들 수 있는 설정 |");
        return b.ToString();
    }

    private static string CalibrationText(ProjectFile project)
    {
        var s = project.Options.Calibration;
        var count = project.Steps.TryGetValue("convert", out var r) && r.Facts.TryGetValue("images", out var n) ? n : s.Count.ToString(CultureInfo.InvariantCulture);
        return s.Source switch
        {
            "samples" => $"툴킷 기본 샘플 사진 {count}장",
            "npy" => $"NPY 텐서 {count}개",
            _ => $"사용자 사진 {count}장",
        };
    }

    private static string Nums(double[] values) => "[" + string.Join(", ", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture))) + "]";

    private static string Size(long bytes) => bytes >= 1048576
        ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>A folder name from the project name: characters Windows does not allow are replaced.</summary>
    public static string SafeFolderName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>
    /// The folder to write into. A folder this app exported before is replaced; any other existing folder is left alone
    /// and a numbered sibling is used instead.
    /// </summary>
    private static string PrepareRoot(string destination, string name)
    {
        Directory.CreateDirectory(destination);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(destination, n == 1 ? name : $"{name} ({n})");
            if (!Directory.Exists(candidate))
                return Directory.CreateDirectory(candidate).FullName;
            if (File.Exists(Path.Combine(candidate, MarkerFile)))
            {
                Directory.Delete(candidate, recursive: true);
                return Directory.CreateDirectory(candidate).FullName;
            }
            if (!Directory.EnumerateFileSystemEntries(candidate).Any())
                return candidate;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
