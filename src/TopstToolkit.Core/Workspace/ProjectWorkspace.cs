using System.Globalization;

namespace TopstToolkit.Core.Workspace;

/// <summary>
/// One project's folders (design §4.3):
/// <code>
/// inputs\model  inputs\labels  inputs\calib  inputs\test     staged copies with ASCII names
/// jobs\&lt;step&gt;-&lt;nnn&gt;\EnlightSDK\enlight\code_emission\cfg\    tool working directories
/// out\networks  out\code  out\result  out\dump  out\perf  out\compat
/// logs\&lt;time&gt;_&lt;step&gt;.out.log / .err.log
/// </code>
/// </summary>
public sealed class ProjectWorkspace
{
    public ProjectWorkspace(WorkspaceRoot workspace, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || NameRules.ToAsciiStem(id, "") != id)
            throw new ArgumentException($"Invalid project id: {id}", nameof(id));
        Workspace = workspace;
        Id = id;
    }

    public WorkspaceRoot Workspace { get; }
    public string Id { get; }
    public string Root => Path.Combine(Workspace.ProjectsDir, Id);

    public string InputsDir => Path.Combine(Root, "inputs");
    public string ModelInputDir => Path.Combine(InputsDir, "model");
    public string LabelsInputDir => Path.Combine(InputsDir, "labels");
    public string CalibrationDir => Path.Combine(InputsDir, "calib");
    public string TestImagesDir => Path.Combine(InputsDir, "test");

    public string JobsDir => Path.Combine(Root, "jobs");
    public string OutDir => Path.Combine(Root, "out");
    public string NetworksDir => Path.Combine(OutDir, "networks");
    public string CodeDir => Path.Combine(OutDir, "code");
    public string ResultDir => Path.Combine(OutDir, "result");
    public string DumpDir => Path.Combine(OutDir, "dump");
    public string PerfDir => Path.Combine(OutDir, "perf");
    public string CompatDir => Path.Combine(OutDir, "compat");
    /// <summary>The check that runs when a model is brought in: its compatibility table and the graph analysis (design §5.7).</summary>
    public string CheckDir => Path.Combine(OutDir, "check");
    /// <summary>
    /// The part of the model behind its cut as board code (design §5.8, stage 나): the generated source, the helpers'
    /// answers and the values the PC computed for the check pictures (<see cref="Pipeline.TailFiles"/>). Made on demand.
    /// </summary>
    public string TailDir => Path.Combine(OutDir, "tail");
    public string LogsDir => Path.Combine(Root, "logs");

    public ProjectWorkspace EnsureCreated()
    {
        foreach (var dir in new[]
                 {
                     ModelInputDir, LabelsInputDir, CalibrationDir, TestImagesDir, JobsDir,
                     NetworksDir, CodeDir, ResultDir, DumpDir, PerfDir, CompatDir, LogsDir,
                 })
            Directory.CreateDirectory(dir);
        return this;
    }

    /// <summary>
    /// A fresh tool working directory <c>jobs\&lt;step&gt;-&lt;nnn&gt;\</c> with the empty
    /// <c>EnlightSDK\enlight\code_emission\cfg\</c> folder the compiler writes <c>hw_config.py</c> into (feature/09 §5.2).
    /// </summary>
    public string NewJobDirectory(string step)
    {
        var safeStep = NameRules.ToAsciiStem(step, "job");
        Directory.CreateDirectory(JobsDir);
        for (var n = 1; n < 10_000; n++)
        {
            var dir = Path.Combine(JobsDir, $"{safeStep}-{n.ToString("000", CultureInfo.InvariantCulture)}");
            if (Directory.Exists(dir))
                continue;
            Directory.CreateDirectory(Path.Combine(dir, "EnlightSDK", "enlight", "code_emission", "cfg"));
            return dir;
        }
        throw new IOException($"Too many job folders for step '{step}'.");
    }

    /// <summary><c>20261120-153012_convert</c>; the runner adds <c>.out.log</c> / <c>.err.log</c>.</summary>
    public string NewLogName(string step, DateTime? now = null)
    {
        var baseName = $"{(now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{NameRules.ToAsciiStem(step, "job")}";
        var name = baseName;
        for (var n = 2; File.Exists(Path.Combine(LogsDir, name + ".out.log")); n++)
            name = $"{baseName}-{n}";
        return name;
    }
}
