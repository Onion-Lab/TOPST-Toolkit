using System.Security.Cryptography;

namespace TopstToolkit.Engine.Board;

/// <summary>
/// The code the app writes for the part of a model behind a cut (design §5.8, stage 나): <c>engine\helpers\gen_tail.py</c>
/// turns the operators behind the cut in the original ONNX file into C that computes the model's own outputs from the
/// NPU output bytes. Like a fast source (<see cref="FastPostProcess"/>) it takes the place of the compiler's
/// <c>post_process.c</c> in <c>net.so</c> and includes it, so the toolkit's code stays in the library as
/// <c>run_post_process_reference</c>. Unlike a fast source it is made for each project (<c>out\tail\</c>).
/// </summary>
public static class TailCode
{
    /// <summary>File name of the generated source, in the project's tail folder and in the build folder.</summary>
    public const string SourceName = "topst_tail.c";

    /// <summary>The kind recorded for a library built with generated tail code (<see cref="FastPostProcess.KindOf"/>).</summary>
    public const string Kind = "tail";

    /// <summary>Runs on the board: compares what the generated code computes with what the PC computed (<c>engine\board</c>).</summary>
    public const string BoardCheckScript = "check_tail.py";

    /// <summary>The helpers whose code decides what is generated: a change in them builds the library again.</summary>
    public static readonly IReadOnlyList<string> GeneratorFiles = ["tail_graph.py", "gen_tail.py"];

    /// <summary>
    /// What was measured on a TOPST AI-G board (one Cortex-A53 core, the dumped NPU outputs of the sample picture,
    /// 2026-11-22), as the package README says it. Change together with design §5.8.
    /// </summary>
    public const string Measured =
        "TOPST AI-G(Cortex-A53) 한 코어에서 yolov8m의 잘라낸 구간(연산 24개, 출력 1 × 84 × 8400)이 한 프레임에 4.3 ms, " +
        "mb2-ssd-lite의 구간(연산 16개, 점수와 상자)이 0.54 ms였습니다. 같은 yolov8m을 종류 \"기타\"로 빌드했을 때 툴킷이 넣는 참고 코드는 113 ms였습니다 (2026-11-22 측정).";

    /// <summary>A hash of the generator's source files, for the cache key of the library step; empty when one is missing.</summary>
    public static string GeneratorHash(EngineLayout layout)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in GeneratorFiles)
        {
            var path = layout.HelperScript(name);
            if (!File.Exists(path))
                return "";
            sha.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..32];
    }
}
