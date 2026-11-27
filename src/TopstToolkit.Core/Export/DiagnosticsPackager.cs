using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.Core.Export;

/// <summary>
/// Collects what a developer needs to look into a problem (design §4.8): the project settings, every tool log,
/// the compatibility tables and the versions. The model and the pictures are never included.
/// </summary>
public sealed class DiagnosticsPackager(ProjectStore store)
{
    /// <summary>Writes <c>topst-diagnostics-&lt;project id&gt;-&lt;time&gt;.zip</c> into <paramref name="destination"/> and returns its path.</summary>
    public string Create(ProjectFile project, string destination, string appVersion, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        var folders = store.FoldersOf(project);
        Directory.CreateDirectory(destination);
        var zipPath = Path.Combine(destination, $"topst-diagnostics-{project.Id}-{time.LocalDateTime:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddFile(zip, Path.Combine(folders.Root, ProjectStore.FileName), ProjectStore.FileName);
        AddFolder(zip, folders.LogsDir, "logs");
        AddFolder(zip, folders.CompatDir, "compat");
        AddFolder(zip, folders.CheckDir, "check"); // the check made when the model came in: compatibility table, graph analysis
        // small text outputs of the compiler help too; the binaries stay out
        if (Directory.Exists(folders.CodeDir))
            foreach (var dir in Directory.EnumerateDirectories(folders.CodeDir))
                foreach (var name in new[] { "compile_debug.log", "network_buf.txt", "network_group_info.csv" })
                    AddFile(zip, Path.Combine(dir, name), $"code/{Path.GetFileName(dir)}/{name}");

        var info = new StringBuilder();
        info.AppendLine($"TOPST Toolkit {appVersion}");
        info.AppendLine($"project      : {project.Id} (schema {project.Schema}, app {project.Versions.App}, sdk {project.Versions.Sdk})");
        info.AppendLine($"created      : {time:O}");
        info.AppendLine($"os           : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        info.AppendLine($"runtime      : {RuntimeInformation.FrameworkDescription}");
        info.AppendLine($"processors   : {Environment.ProcessorCount}");
        info.AppendLine($"memory       : {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576} MiB available to the process");
        var entry = zip.CreateEntry("info.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            writer.Write(info.ToString());
        return zipPath;
    }

    private static void AddFolder(ZipArchive zip, string folder, string prefix)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            AddFile(zip, file, prefix + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
    }

    private static void AddFile(ZipArchive zip, string file, string entryName)
    {
        if (!File.Exists(file))
            return;
        try
        {
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
        catch (IOException)
        {
            // a log still being written is skipped rather than failing the whole package
        }
    }
}
