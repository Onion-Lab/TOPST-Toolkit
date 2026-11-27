using TopstToolkit.Core.Presets;

namespace TopstToolkit.Core.Projects;

/// <summary>
/// The file a detector needs besides its model: the YOLO grid (<c>.bin</c>) or the SSD anchors (<c>.anchor</c>), kept in the
/// project's <c>inputs\model</c> folder and named in <see cref="ConvertOptions.PostProcess"/>.
/// </summary>
public static class Companions
{
    /// <summary>The extension of a preset's companion kind: <c>grid</c> → <c>.bin</c>, <c>anchor</c> → <c>.anchor</c>, <c>weights</c> → <c>.weights</c>.</summary>
    public static string? ExtensionOf(string? kind) => kind switch
    {
        "grid" => ".bin",
        "anchor" => ".anchor",
        "weights" => ".weights",
        _ => null,
    };

    /// <summary>
    /// Puts the grid or anchor file the project already holds back into the settings when they lost it. Choosing another kind
    /// in 2 설정 clears it (a grid is no anchor file), and choosing the detector again used to leave it empty although the
    /// file — for a YOLOv8 export the grid the app made at import — was still in <c>inputs\model</c> (seen 2026-11-22: the
    /// build then stopped at the conversion). True when the project changed.
    /// </summary>
    public static bool Restore(ProjectStore store, ProjectFile project)
    {
        var kind = PresetCatalog.Find(project.Preset)?.Companion;
        if (kind is not ("grid" or "anchor") || project.Model is not { } model || ProjectStore.IsDraft(project))
            return false;
        var convert = project.Options.Convert;
        if (convert.PostProcess is { } current && File.Exists(store.ToAbsolute(project, current)))
            return false;
        var folder = store.FoldersOf(project).ModelInputDir;
        if (!Directory.Exists(folder))
            return false;

        // the toolkit's naming: yolov5m_extracted.onnx goes with yolov5m.bin; the app's cut keeps the stem of the original
        var extension = ExtensionOf(kind)!;
        var stem = Path.GetFileNameWithoutExtension(model.Original ?? model.Staged);
        var baseStem = stem.EndsWith("_extracted", StringComparison.OrdinalIgnoreCase) ? stem[..^"_extracted".Length] : stem;
        var found = new[] { baseStem + extension, stem + extension }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists)
                    ?? Directory.EnumerateFiles(folder, "*" + extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (found is null)
            return false;
        convert.PostProcess = store.ToRelative(project, found);
        return true;
    }
}
