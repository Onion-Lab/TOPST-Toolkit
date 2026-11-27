namespace TopstToolkit.Core.Workspace;

/// <summary>
/// Where the app puts what the user keeps (2026-11-25, the user's wish): <c>내 문서\TOPST Toolkit</c>, made when the app
/// is installed (Setup runs <c>TopstToolkit.exe --prepare-user</c> as the user who installs it) and again on every start
/// for anyone else on the PC. Exports and board results get folders of their own in it; every save dialog starts there
/// and the user can still pick any other place. Projects stay in the workspace: the toolkit needs an ASCII path, and
/// Documents often is not one (OneDrive, a Korean folder name such as <c>OneDrive\문서</c>).
/// </summary>
public static class SaveFolders
{
    public const string FolderName = "TOPST Toolkit";
    /// <summary>Board packages from 7 내보내기 (one folder per project in it).</summary>
    public const string ExportsName = "내보내기";
    /// <summary>Pictures, videos and tables the board made (보드 › 실행), and long-run records.</summary>
    public const string BoardResultsName = "보드 결과";

    /// <summary>Tests and screen captures: another folder in place of Documents.</summary>
    public const string EnvironmentVariable = "TOPST_SAVE_ROOT";

    /// <summary><c>내 문서\TOPST Toolkit</c>, or the folder the user chose in 설정 (<paramref name="configured"/>).</summary>
    public static string Root(string? configured = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        if ((getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable) is { Length: > 0 } custom)
            return Path.GetFullPath(custom);
        if (configured is { Length: > 0 } chosen)
            return Path.GetFullPath(chosen);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderName);
    }

    public static string Exports(string? configured = null) => Path.Combine(Root(configured), ExportsName);
    public static string BoardResults(string? configured = null) => Path.Combine(Root(configured), BoardResultsName);

    /// <summary>Makes the folder and its two subfolders; true when they are there. Never throws: a save dialog still works without them.</summary>
    public static bool EnsureCreated(string? configured = null)
    {
        try
        {
            Directory.CreateDirectory(Exports(configured));
            Directory.CreateDirectory(BoardResults(configured));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The folder a save dialog starts in: <paramref name="folder"/>, made if it is missing; null when it cannot be made.</summary>
    public static string? Prepared(string folder)
    {
        try
        {
            return Directory.CreateDirectory(folder).FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
