namespace TopstToolkit.Core.Components;

/// <summary>
/// One line of the install screen (docs/mockups/mockup-components.html ②): the installer stages it covers
/// (<see cref="ComponentProgress.Key"/>), a weight for the overall progress, and the bytes it downloads.
/// </summary>
public sealed record InstallStepPlan(string Title, string? Detail, IReadOnlyList<string> Keys, double Weight, long Bytes = 0)
{
    public bool Covers(string key) => Keys.Contains(key);
}

/// <summary>
/// The lines the install screen shows for each component, in the order the installer goes through them. Downloads start
/// together at the beginning (<see cref="ComponentInstaller.PrefetchAsync"/>), so a download line further down can run
/// while an earlier line is still busy; the installer itself only moves forward.
/// </summary>
public static class ComponentSteps
{
    public static IReadOnlyList<InstallStepPlan> For(ComponentSpec spec)
    {
        long Bytes(params string[] ids) => spec.Downloads.Where(d => ids.Contains(d.Id)).Sum(d => d.Bytes);
        return spec.Id switch
        {
            // the toolkit zip after the packages: pip installs while the big zip is still coming in (2026-11-25)
            ComponentCatalog.Engine =>
            [
                new("Python 3.8.10 · pip 24.0 받기", null, ["download:python", "download:pip"], 2, Bytes("python", "pip")),
                new($"Python 패키지 {PackageCount(spec)}개 받아 설치", "PyTorch 등 · 몇 분 걸립니다", ["python", "pip", "packages", "extras"], 45,
                    (spec.Packages?.Bytes ?? 0) + spec.Extras.Sum(e => e.Bytes)),
                new("TOPST AI-G NPU Toolkit 받기", "TOPST 공식 링크 · ENLIGHT SDK 0.9.9", ["download:toolkit"], 40, Bytes("toolkit")),
                new("툴킷에서 필요한 부분 꺼내기", null, ["toolkit-wheels", "prune", "extract"], 5),
                new("엔진이 도는지 확인", "SDK와 PyTorch를 한 번 불러 봅니다", ["smoke", "finish"], 8),
            ],
            ComponentCatalog.BoardTools =>
            [
                new("Arm 크로스 컴파일러 받기", "Arm 공식 배포본 · GNU-A 9.2-2019.12", ["download:toolchain"], 25, Bytes("toolchain")),
                new("필요한 파일만 꺼내기", "압축 파일을 한 번 다 읽어서 조금 걸립니다", ["archive"], 15),
                new("컴파일러가 도는지 확인", "작은 보드용 라이브러리를 하나 만들어 봅니다", ["smoke", "finish"], 3),
            ],
            ComponentCatalog.Zoo =>
            [
                new("Python 패키지 받아 설치", "timm · Ultralytics · onnx · onnxslim 등 · 엔진과 따로 둡니다", ["extras", "prune"], 85, spec.Extras.Sum(e => e.Bytes)),
                new("내보내기 도구가 도는지 확인", "한 번 불러 봅니다", ["smoke", "finish"], 15),
            ],
            _ => [new(spec.Title + " 설치", null, [], 1)],
        };
    }

    /// <summary>The line a stage belongs to, or -1.</summary>
    public static int IndexOf(IReadOnlyList<InstallStepPlan> plan, string key)
    {
        for (var i = 0; i < plan.Count; i++)
            if (plan[i].Covers(key))
                return i;
        return -1;
    }

    /// <summary>
    /// The overall fraction of an install of several components: whole components done, plus the lines done in the
    /// current one, plus the part of the current line (a download's bytes).
    /// </summary>
    public static double Overall(IReadOnlyList<IReadOnlyList<InstallStepPlan>> plans, int component, int line, double lineFraction)
    {
        var total = plans.Sum(p => p.Sum(s => s.Weight));
        if (total <= 0)
            return 0;
        var done = plans.Take(component).Sum(p => p.Sum(s => s.Weight));
        if (component < plans.Count)
        {
            var current = plans[component];
            done += current.Take(Math.Max(0, line)).Sum(s => s.Weight);
            if (line >= 0 && line < current.Count)
                done += current[line].Weight * Math.Clamp(lineFraction, 0, 1);
        }
        return Math.Clamp(done / total, 0, 1);
    }

    /// <summary>
    /// The overall fraction when lines can run side by side: every line's weight times how far it is (1 when done,
    /// its own fraction while it runs, 0 before), over all components.
    /// </summary>
    public static double Overall(IReadOnlyList<IReadOnlyList<InstallStepPlan>> plans, Func<int, int, double> lineFraction)
    {
        var total = plans.Sum(p => p.Sum(s => s.Weight));
        if (total <= 0)
            return 0;
        var done = 0.0;
        for (var c = 0; c < plans.Count; c++)
            for (var l = 0; l < plans[c].Count; l++)
                done += plans[c][l].Weight * Math.Clamp(lineFraction(c, l), 0, 1);
        return Math.Clamp(done / total, 0, 1);
    }

    /// <summary>
    /// How much of a line its bytes stand for: all of a download line; 60% of a line that downloads and then installs
    /// ("Python 패키지 받아 설치"), so the bar does not stand still at the line's end while pip still runs.
    /// </summary>
    public static double BytesShare(InstallStepPlan line) => line.Keys.All(k => k.StartsWith("download:", StringComparison.Ordinal)) ? 1 : 0.6;

    /// <summary>The number of locked packages (lines with a hash in the lock file); 36 when the file cannot be read.</summary>
    public static int PackageCount(ComponentSpec spec, string? catalogDirectory = null)
    {
        if (spec.Packages is null || catalogDirectory is null)
            return 36;
        var path = Path.Combine(catalogDirectory, spec.Packages.Lock);
        return File.Exists(path) ? File.ReadLines(path).Count(l => l.Contains("--hash=", StringComparison.Ordinal)) : 36;
    }

    /// <summary>"1.5 GB", "337 MB", "8 MB" (powers of 1024, as the screens and the README count them).</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        > 0 => Math.Max(1, bytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => "0 MB",
    };
}
