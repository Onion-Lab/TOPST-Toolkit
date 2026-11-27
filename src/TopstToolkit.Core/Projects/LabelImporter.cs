using System.Text;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.Core.Projects;

/// <param name="Count">Number of class names found.</param>
/// <param name="HasDisplayNames">The file had names the toolkit cannot take (Korean and so on); they are kept for the screen only.</param>
public sealed record LabelImportResult(int Count, bool HasDisplayNames, IReadOnlyList<string> Names);

/// <summary>
/// Brings the user's own class names into a project. The toolkit accepts ASCII labels only (feature/09 §4), so a file
/// with Korean names is split in two: an ASCII file for the toolkit (<c>class_3</c> in place of a Korean name) and the
/// original names for the screen, which the app draws itself (design §5.5).
/// </summary>
public static class LabelImporter
{
    public const string ToolkitFileName = "custom.label";
    public const string DisplayFileName = "display.txt";

    public static LabelImportResult Import(ProjectStore store, ProjectFile project, string sourceFile)
    {
        var names = ReadNames(sourceFile);
        if (names.Count == 0)
            throw new InvalidDataException("라벨 파일에 이름이 없어요. 한 줄에 클래스 이름 하나씩 적어 주세요.");

        var dir = Directory.CreateDirectory(store.FoldersOf(project).LabelsInputDir).FullName;
        var toolkit = names.Select((n, i) => NameRules.IsAscii(n) ? n : $"class_{i}").ToList();
        var toolkitPath = Path.Combine(dir, ToolkitFileName);
        File.WriteAllText(toolkitPath, string.Join("\n", toolkit) + "\n", new UTF8Encoding(false));

        var hasDisplay = names.Any(n => !NameRules.IsAscii(n));
        var displayPath = Path.Combine(dir, DisplayFileName);
        if (hasDisplay)
            File.WriteAllText(displayPath, string.Join("\n", names) + "\n", new UTF8Encoding(false));
        else if (File.Exists(displayPath))
            File.Delete(displayPath);

        project.Options.Labels.Set = "custom";
        project.Options.Labels.Toolkit = store.ToRelative(project, toolkitPath);
        project.Options.Labels.Display = hasDisplay ? store.ToRelative(project, displayPath) : null;
        return new LabelImportResult(names.Count, hasDisplay, names);
    }

    /// <summary>The names to show for each class number, when the project has its own; otherwise null.</summary>
    public static IReadOnlyList<string>? DisplayNames(ProjectStore store, ProjectFile project)
    {
        if (project.Options.Labels.Display is not { } relative)
            return null;
        var file = store.ToAbsolute(project, relative);
        return File.Exists(file) ? ReadNames(file) : null;
    }

    /// <summary>One name per line; empty lines and a UTF-8 byte order mark are ignored. Reads cp949 files too.</summary>
    public static IReadOnlyList<string> ReadNames(string file)
    {
        var bytes = File.ReadAllBytes(file);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Korean Windows saves "ANSI" text as cp949
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(949).GetString(bytes);
        }
        return text.TrimStart('﻿').Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0).ToList();
    }
}
