namespace TopstToolkit.Core.Workspace;

/// <summary>
/// Keeps a project folder from growing without end: tool logs and the working folders of finished tool runs are
/// removed once they are older than the retention the user set (설정 → 실행 기록 보관). Inputs and build outputs
/// are never touched.
/// </summary>
public static class Housekeeping
{
    /// <summary>Returns how many log files and job folders were removed. Files in use are skipped.</summary>
    public static int Clean(ProjectWorkspace project, int retentionDays, DateTime? now = null)
    {
        if (retentionDays < 1)
            return 0;
        var limit = (now ?? DateTime.Now).AddDays(-retentionDays);
        var removed = 0;

        if (Directory.Exists(project.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(project.LogsDir, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < limit)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        if (Directory.Exists(project.JobsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(project.JobsDir))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) < limit)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return removed;
    }
}
