using TopstToolkit.Engine.Commands;

namespace TopstToolkit.Engine.Board;

/// <summary>
/// Builds the board library <c>net.so</c> the way the toolkit's <c>build_network/Makefile</c> does (SDK 0.9.9),
/// without <c>make</c>: one gcc call that compiles the eight sources and one that links them (design §11 D5, feature/08).
/// The flags are the Makefile's, unchanged, so the result matches the official Linux procedure
/// (including its quirks: <c>-pg</c> at compile time only, <c>-D__i386__</c>, no <c>-pthread</c> at link time).
/// </summary>
public static class NetSoBuild
{
    public const string OutputName = "net.so";

    /// <summary>The two files the compiler generates per network; everything else is fixed runtime source.</summary>
    public static readonly IReadOnlyList<string> GeneratedFiles = ["network.h", "post_process.c"];

    /// <summary>Makefile <c>CFLAGS</c>, in order, after the leading <c>-fPIC</c> of the compile rule.</summary>
    public static readonly IReadOnlyList<string> CompileFlags =
    [
        "-fPIC",
        "-Wall", "-D__NO_INLINE__", "-std=gnu99",
        "-lpthread", "-pthread",
        "-O2", "-g",
        "-D__i386__",
        "-pg",
        "-Wno-unused", "-Wno-attributes", "-fno-delete-null-pointer-checks",
        "-fPIC", "-no-pie",
        "-Ienlight", "-Inetwork",
    ];

    /// <summary>Makefile <c>SRCS</c>, relative to the build folder.</summary>
    public static readonly IReadOnlyList<string> Sources =
    [
        "post_process.c",
        "classifier/classifier.c",
        "detector/detector.c",
        "detector/yolo_detector.c",
        "detector/ssd_detector.c",
        "custom_postproc/custom_postproc.c",
        "enlight/enlight_network.c",
        "network.c",
    ];

    /// <summary>Makefile <c>OBJS</c>: gcc -c drops each object into the build folder.</summary>
    public static readonly IReadOnlyList<string> Objects =
    [
        "post_process.o", "detector.o", "classifier.o", "yolo_detector.o",
        "ssd_detector.o", "custom_postproc.o", "enlight_network.o", "network.o",
    ];

    /// <summary>Fixed runtime folders and files copied from <c>toolkit\build_network</c>.</summary>
    private static readonly string[] RuntimeFolders = ["classifier", "detector", "custom_postproc", "enlight"];
    private static readonly string[] RuntimeFiles = ["network.c"];

    /// <summary>
    /// Two additions to the Makefile flags keep local paths out of <c>net.so</c>. Without them the library records the
    /// build folder (DWARF comp_dir), the toolchain folder (DWARF include directories) and the full path of
    /// <c>crti.o</c>/<c>crtn.o</c> (file symbols) — which would leak the Windows user name of whoever built it and make
    /// every PC produce a different file. With them the output is byte-identical for any build folder, install folder
    /// and for the pruned or the full toolchain (verified 2026-11-20). Code, data and the dynamic symbol table are
    /// unchanged; only debug strings and local symbols differ from the official build.
    /// </summary>
    public static IReadOnlyList<string> PathNeutralCompileFlags(string gcc, string buildDir) =>
    [
        "-ffile-prefix-map=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDir)) + "=.",
        "-ffile-prefix-map=" + ToolchainRoot(gcc) + "=/toolchain",
    ];

    /// <summary>Drops local symbols, among them the file symbols ld makes from the absolute crti.o/crtn.o paths.</summary>
    public const string PathNeutralLinkFlag = "-Wl,--discard-all";

    /// <summary><c>&lt;root&gt;inarch64-none-linux-gnu-gcc.exe</c> → <c>&lt;root&gt;</c>.</summary>
    public static string ToolchainRoot(string gcc) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(gcc))) ?? throw new ArgumentException("Unexpected gcc path: " + gcc);

    /// <summary>The compiler's generated source that a fast source (<see cref="FastPostProcess"/>) takes the place of.</summary>
    public const string GeneratedPostProcess = "post_process.c";

    /// <summary>
    /// Flags of the one extra gcc call for the app's own fast post-processing source (design §5.8). They apply to that
    /// file only; the toolkit's sources keep the Makefile flags. Differences: optimized for the board's CPU
    /// (<c>-O3 -mcpu=cortex-a53</c>), no profiling hooks and no debug information (<c>-pg -g</c>), and
    /// <c>-ffp-contract=off</c> so that a multiply-add is fused only where the source says so — the results have to
    /// match the toolkit code bit for bit. <c>-D__i386__</c> is the Makefile's switch that makes the log go to printf.
    /// </summary>
    public static readonly IReadOnlyList<string> FastCompileFlags =
    [
        "-fPIC",
        "-Wall", "-std=gnu99",
        "-O3", "-mcpu=cortex-a53", "-ffp-contract=off",
        "-D__i386__",
        "-Wno-attributes", "-fno-delete-null-pointer-checks",
        "-Ienlight", "-Inetwork",
    ];

    /// <summary>The Makefile's sources; with a fast source, without the generated file it includes.</summary>
    public static IReadOnlyList<string> SourcesFor(string? fastSource) =>
        fastSource is null ? Sources : [.. Sources.Where(s => s != GeneratedPostProcess)];

    /// <summary>The objects to link; with a fast source, its object instead of <c>post_process.o</c>.</summary>
    public static IReadOnlyList<string> ObjectsFor(string? fastSource) =>
        fastSource is null ? Objects : [.. Objects.Select(o => o == "post_process.o" ? Path.ChangeExtension(fastSource, ".o") : o)];

    /// <param name="fastSource">File name of a fast source in the build folder, or null for the official build.</param>
    public static ToolInvocation Compile(string gcc, string buildDir, string? fastSource = null) =>
        new(ToolKind.Native, gcc, [.. CompileFlags, .. PathNeutralCompileFlags(gcc, buildDir), "-c", .. SourcesFor(fastSource)], "보드용 라이브러리 컴파일");

    /// <summary>The extra call that compiles the fast source (which includes the generated <c>post_process.c</c>).</summary>
    public static ToolInvocation CompileFast(string gcc, string buildDir, string fastSource) =>
        new(ToolKind.Native, gcc, [.. FastCompileFlags, .. PathNeutralCompileFlags(gcc, buildDir), "-c", fastSource],
            fastSource == TailCode.SourceName ? "잘라낸 구간 코드 컴파일" : "빠른 박스 계산 코드 컴파일");

    /// <summary>
    /// The Makefile's compile call for the generated <c>post_process.c</c> alone: the way back to the official
    /// library when a fast source does not compile against the runtime at hand.
    /// </summary>
    public static ToolInvocation CompileGenerated(string gcc, string buildDir) =>
        new(ToolKind.Native, gcc, [.. CompileFlags, .. PathNeutralCompileFlags(gcc, buildDir), "-c", GeneratedPostProcess], "보드용 라이브러리 컴파일");

    public static ToolInvocation Link(string gcc, string? fastSource = null) =>
        new(ToolKind.Native, gcc, ["-shared", "-Wl,-soname," + OutputName, PathNeutralLinkFlag, .. LibraryFlags(gcc), "-o", OutputName, .. ObjectsFor(fastSource)], "보드용 라이브러리 링크");

    /// <summary>
    /// The folder with <c>libgcc_s.so</c>, named on the link line. gcc finds it by itself only while the toolchain's path
    /// has no spaces: under <c>C:\Program Files\TOPST Toolkit\components</c> ld stopped with "cannot find -lgcc_s"
    /// (2026-11-25). Named explicitly it links from any folder, and the library is the same file it would have found.
    /// </summary>
    public static IReadOnlyList<string> LibraryFlags(string gcc) =>
        ["-L", Path.Combine(ToolchainRoot(gcc), "aarch64-none-linux-gnu", "lib64")];

    /// <summary>
    /// Fills <paramref name="buildDir"/> with the fixed runtime sources and the network's own
    /// <c>network.h</c> / <c>post_process.c</c> from the compiler output folder. The example copies of those two
    /// files that ship in <c>build_network</c> come from an older SDK and are never used (feature/08 §2).
    /// </summary>
    /// <param name="fastSourcePath">Full path of a fast source to put next to them, or null.</param>
    public static void PrepareSources(string runtimeSourceDir, string compilerOutputDir, string buildDir, string? fastSourcePath = null)
    {
        if (fastSourcePath is not null)
        {
            Directory.CreateDirectory(buildDir);
            File.Copy(fastSourcePath, Path.Combine(buildDir, Path.GetFileName(fastSourcePath)), overwrite: true);
        }
        Directory.CreateDirectory(buildDir);
        foreach (var folder in RuntimeFolders)
        {
            var from = Path.Combine(runtimeSourceDir, folder);
            if (!Directory.Exists(from))
                throw new DirectoryNotFoundException($"Board runtime source folder is missing: {from}");
            var to = Path.Combine(buildDir, folder);
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
                if (Path.GetExtension(file) is ".c" or ".h")
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var file in RuntimeFiles)
            File.Copy(Path.Combine(runtimeSourceDir, file), Path.Combine(buildDir, file), overwrite: true);
        foreach (var file in GeneratedFiles)
        {
            var from = Path.Combine(compilerOutputDir, file);
            if (!File.Exists(from))
                throw new FileNotFoundException($"The compiler output has no {file}.", from);
            File.Copy(from, Path.Combine(buildDir, file), overwrite: true);
        }
    }

    /// <summary>The first gcc diagnostic worth showing (<c>file:line:col: error: ...</c> or a linker error).</summary>
    public static string? FirstError(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).FirstOrDefault(l =>
            l.Contains(": error:", StringComparison.Ordinal) || l.Contains("fatal error", StringComparison.Ordinal)
            || l.Contains("undefined reference", StringComparison.Ordinal) || l.Contains("ld.exe:", StringComparison.Ordinal)
            || l.Contains("cannot find", StringComparison.Ordinal));
}
