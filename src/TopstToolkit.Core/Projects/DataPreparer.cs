using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;

namespace TopstToolkit.Core.Projects;

/// <param name="Files">Staged files inside the project, in the order the toolkit reads them.</param>
/// <param name="Warnings">Plain sentences for the data screen: too few pictures, fewer than asked for, ...</param>
public sealed record CalibrationSummary(IReadOnlyList<string> Files, int DuplicatesSkipped, int UnsupportedSkipped, IReadOnlyList<string> Warnings)
{
    public int Count => Files.Count;
}

/// <summary>
/// Step 3 "데이터": copies the calibration pictures (or NPY tensors) and the pictures used to check the result into
/// the project under ASCII names. Calibration decides the value ranges the INT8 network uses, so the set is part of the
/// build cache key (design §4.5).
/// </summary>
public sealed class DataPreparer(ProjectStore store, EngineLayout? layout)
{
    /// <summary>Below this the value ranges are not trustworthy; the toolkit's examples use 100.</summary>
    public const int RecommendedMinimum = 20;
    public const int MaxTestImages = 20;

    public bool IsNpy(ProjectFile project) => project.Options.Calibration.Source == "npy";

    /// <summary>Copies the calibration set the project's settings describe, replacing what was staged before.</summary>
    /// <exception cref="FileNotFoundException">The chosen folder does not exist.</exception>
    public CalibrationSummary StageCalibration(ProjectFile project)
    {
        var folders = store.FoldersOf(project);
        var settings = project.Options.Calibration;
        var source = settings.Source == "samples" || string.IsNullOrEmpty(settings.SourcePath)
            ? layout?.DefaultCalibrationDir ?? throw new FileNotFoundException("기본 샘플 사진을 찾지 못했어요.")
            : settings.SourcePath;
        if (!Directory.Exists(source))
            throw new FileNotFoundException("고른 폴더를 찾지 못했어요.", source);

        var staged = FileStager.StageDataFiles([source], folders.CalibrationDir, IsNpy(project) ? FileStager.NpyExtensions : FileStager.ImageExtensions,
            limit: Math.Max(1, settings.Count));
        settings.ListSha256 = staged.ListSha256;
        store.Save(project);
        return Summarize(project, staged.Files.Select(f => f.Staged).ToList(), staged.DuplicatesSkipped, staged.UnsupportedSkipped);
    }

    /// <summary>What is staged now, without copying anything.</summary>
    public CalibrationSummary Describe(ProjectFile project)
    {
        var dir = store.FoldersOf(project).CalibrationDir;
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return Summarize(project, files, 0, 0);
    }

    private CalibrationSummary Summarize(ProjectFile project, IReadOnlyList<string> files, int duplicates, int unsupported)
    {
        var warnings = new List<string>();
        var unit = IsNpy(project) ? "개" : "장";
        var thing = IsNpy(project) ? "파일" : "사진";
        if (files.Count == 0)
            warnings.Add($"쓸 수 있는 {thing}이 없어요. 폴더를 다시 골라 주세요.");
        else if (files.Count < RecommendedMinimum)
            warnings.Add($"{thing}이 {files.Count}{unit}뿐이에요. {RecommendedMinimum}{unit} 이상이면 결과가 더 안정적입니다.");
        if (files.Count > 0 && files.Count < project.Options.Calibration.Count && duplicates + unsupported == 0)
            warnings.Add($"폴더에 {thing}이 {files.Count}{unit}만 있어서 그만큼만 씁니다.");
        if (duplicates > 0)
            warnings.Add($"내용이 같은 {thing} {duplicates}{unit}은 한 번만 썼어요.");
        return new CalibrationSummary(files, duplicates, unsupported, warnings);
    }

    /// <summary>The pictures the result screen shows, in name order.</summary>
    public IReadOnlyList<string> TestImages(ProjectFile project)
    {
        var dir = store.FoldersOf(project).TestImagesDir;
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : [];
    }

    /// <summary>Puts the bundled sample picture in when the project has none, so a first build has something to show.</summary>
    public IReadOnlyList<string> EnsureTestImages(ProjectFile project)
    {
        var existing = TestImages(project);
        if (existing.Count > 0 || layout is null || IsNpy(project) || !File.Exists(layout.SampleImage))
            return existing;
        FileStager.StageImages([layout.SampleImage], store.FoldersOf(project).TestImagesDir);
        return TestImages(project);
    }

    /// <summary>Adds pictures (files or folders). Identical pictures are skipped, names are made unique. Returns how many were added.</summary>
    public int AddTestImages(ProjectFile project, IEnumerable<string> sources)
    {
        var folders = store.FoldersOf(project);
        var incoming = Path.Combine(folders.InputsDir, "test.incoming");
        try
        {
            var staged = FileStager.StageImages(sources, incoming, limit: MaxTestImages);
            var existing = TestImages(project);
            var knownHashes = existing.Select(FileStager.Sha256Of).ToHashSet(StringComparer.Ordinal);
            var usedStems = existing.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(folders.TestImagesDir);
            var added = 0;
            foreach (var file in staged.Files)
            {
                if (existing.Count + added >= MaxTestImages || !knownHashes.Add(file.Sha256))
                    continue;
                var stem = Path.GetFileNameWithoutExtension(file.Staged);
                var unique = stem;
                for (var n = 2; !usedStems.Add(unique); n++)
                    unique = $"{stem}_{n}";
                File.Move(file.Staged, Path.Combine(folders.TestImagesDir, unique + Path.GetExtension(file.Staged)));
                added++;
            }
            return added;
        }
        finally
        {
            if (Directory.Exists(incoming))
                Directory.Delete(incoming, recursive: true);
        }
    }

    /// <summary>Empties the test folder. Needed when the data kind changes between pictures and NPY tensors.</summary>
    public void ClearTestImages(ProjectFile project)
    {
        var dir = store.FoldersOf(project).TestImagesDir;
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir))
                File.Delete(file);
    }

    /// <summary>Removes one staged test picture. Only files inside the project's test folder are touched.</summary>
    public void RemoveTestImage(ProjectFile project, string stagedPath)
    {
        var dir = Path.GetFullPath(store.FoldersOf(project).TestImagesDir);
        var full = Path.GetFullPath(stagedPath);
        if (string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            File.Delete(full);
    }
}
