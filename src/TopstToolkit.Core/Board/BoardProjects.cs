using TopstToolkit.Core.Export;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Board;

/// <summary>What the board side needs from a project: its files for the board, its letterbox, its class names.</summary>
public static class BoardProjects
{
    /// <summary>
    /// The project's model for the board, or null when it is not built (no compiler output with net.so). Its manifest
    /// carries the class names and the letterbox, so the model runs the same way from any PC.
    /// </summary>
    public static BoardDeployment? Deployment(ProjectStore store, ProjectFile project, string title, EngineLayout? layout)
    {
        if (FilesFor(store, project) is not { } files)
            return null;
        var manifest = BoardAgent.ManifestFor(project.Id, project.Name, title, project.Task ?? "", files,
            ClassNames(store, project, layout), project.Options.Convert.Letterbox);
        return new BoardDeployment(BoardAgent.FolderFor(project.Id), files, manifest);
    }

    /// <summary>The three files the board needs from the project's build, or null when one is missing. Hashes nothing.</summary>
    public static IReadOnlyList<string>? FilesFor(ProjectStore store, ProjectFile project)
    {
        if (BuildPaths.For(store, project) is not { } paths)
            return null;
        var files = BoardAgent.ModelFiles.Select(f => Path.Combine(paths.CodeDir, f)).ToList();
        return files.All(File.Exists) ? files : null;
    }

    /// <summary>The network's buffers in the NPU's reserved memory (code, weights, work, input, output), 0 when unknown.</summary>
    public static long NpuBytesNeeded(ProjectStore store, ProjectFile project) =>
        BuildPaths.For(store, project) is { } paths && NetworkBuffers.ParseFile(Path.Combine(paths.CodeDir, "network_buf.txt")) is { } buffers
            ? buffers.Total
            : 0;

    /// <summary>
    /// The names to show for the project's classes (Korean where the app knows the word, as on the 결과 screen):
    /// the user's own names first, then the label file the toolkit gets. Null when the project has none.
    /// </summary>
    public static IReadOnlyList<string>? ClassNames(ProjectStore store, ProjectFile project, EngineLayout? layout)
    {
        var names = LabelImporter.DisplayNames(store, project)
                    ?? (new BoardPackager(store, layout).LabelsOf(project) is { } file ? LabelImporter.ReadNames(file) : null);
        return names?.Select(LabelNames.Display).ToList();
    }

    /// <summary>
    /// The name of a class the board reports. Detection networks count from 1 (the background's place, feature/08 §2),
    /// classification networks from 0, the same numbers the PC simulation gives.
    /// </summary>
    public static string NameOf(IReadOnlyList<string>? names, string type, int number)
    {
        var index = type == "obj" ? number - 1 : number;
        return names is not null && index >= 0 && index < names.Count ? names[index] : $"클래스 {number}";
    }
}
