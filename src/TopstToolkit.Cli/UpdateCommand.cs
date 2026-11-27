using System.Text.RegularExpressions;
using TopstToolkit.Core.Updates;

namespace TopstToolkit.Cli;

/// <summary>
/// The publisher's side of app updates (design §7.5):
///
///   update key      [--out &lt;file&gt;]          a new signing key (once); prints the public key for UpdateKeys.Trusted
///   update manifest --installer &lt;Setup.exe&gt; (--base &lt;update address&gt; | --url &lt;installer address&gt;)
///                   [--version 0.2.0] [--notes &lt;file&gt;] [--page &lt;https page&gt;] [--key &lt;file&gt;] [--out &lt;folder&gt;]
///                                           update.json + update.json.sig next to the installer
///   update check    [--base &lt;update address&gt;] [--current 0.1.0]
///                                           what an installed app would see at that address
/// </summary>
internal static class UpdateCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<string, string?> option, Action<string> output)
    {
        switch (args.ElementAtOrDefault(0))
        {
            case "key":
                return NewKey(option("--out") ?? UpdatePublisher.DefaultKeyPath, output);
            case "manifest":
                return Manifest(option, output);
            case "check":
                return await CheckAsync(option, output);
            default:
                output("update key | update manifest | update check (see src\\TopstToolkit.Cli\\UpdateCommand.cs)");
                return 2;
        }
    }

    private static int NewKey(string path, Action<string> output)
    {
        if (File.Exists(path))
        {
            output($"이미 있어요: {path}");
            output("열쇠는 덮어쓰지 않습니다. 새로 만들려면 다른 --out 을 주세요.");
            using var existing = UpdateSignature.ReadKeyFile(path);
            output("공개 키: " + UpdateSignature.PublicKeyOf(existing));
            return 1;
        }
        using var key = UpdateSignature.NewKey();
        UpdateSignature.WriteKeyFile(path, key);
        output($"업데이트 서명 열쇠를 만들었어요: {path}");
        output("공개 키 (src\\TopstToolkit.Core\\Updates\\UpdateSignature.cs 의 UpdateKeys.Trusted 에 넣기):");
        output(UpdateSignature.PublicKeyOf(key));
        output("");
        output("이 파일을 잃어버리면 이미 설치된 앱에 업데이트를 보낼 수 없어요. USB 같은 다른 곳에 한 부 백업해 두세요.");
        output("다른 사람에게 주거나 저장소(GitHub)에 올리면 안 됩니다.");
        return 0;
    }

    private static int Manifest(Func<string, string?> option, Action<string> output)
    {
        if (option("--installer") is not { } installer || !File.Exists(installer))
        {
            output("--installer <TOPST-Toolkit-Setup-x.y.z.exe> 가 필요해요.");
            return 2;
        }
        var fileName = Path.GetFileName(installer);
        var version = option("--version") ?? Regex.Match(fileName, @"(\d+\.\d+\.\d+)").Groups[1].Value;
        if (!UpdateVersion.TryParse(version, out _))
        {
            output("버전을 알 수 없어요. --version 0.2.0 처럼 주세요.");
            return 2;
        }
        var url = option("--url") ?? (option("--base") is { } baseUrl ? UpdatePublisher.InstallerUrl(baseUrl, version, fileName) : null);
        if (url is null)
        {
            output("--base <업데이트 주소> 또는 --url <설치 파일 주소> 가 필요해요.");
            return 2;
        }
        var keyPath = option("--key") ?? UpdatePublisher.DefaultKeyPath;
        if (!File.Exists(keyPath))
        {
            output($"서명 열쇠가 없어요: {keyPath} (만들기: TopstToolkit.Cli update key)");
            return 2;
        }
        var notes = option("--notes") is { } notesFile ? UpdatePublisher.ReadNotes(notesFile) : [];
        var outDir = option("--out") ?? Path.GetDirectoryName(Path.GetFullPath(installer))!;
        using var key = UpdateSignature.ReadKeyFile(keyPath);
        // tests sign with a key of their own, which the app takes only from a server on the same PC
        var trusted = option("--trust") is { } extra ? UpdateKeys.Trusted.Append(extra).ToList() : UpdateKeys.Trusted;
        try
        {
            var manifest = UpdatePublisher.Write(installer, version, url, notes, option("--page"), key, outDir, trusted);
            output($"{UpdateManifest.FileName} · {UpdateManifest.SignatureFileName} → {outDir}");
            output($"  버전 {manifest.Version} · {manifest.Installer.Size / 1048576.0:N1} MB · SHA-256 {manifest.Installer.Sha256}");
            output($"  설치 파일 주소 {manifest.Installer.Url}");
            foreach (var note in manifest.Notes)
                output("  · " + note);
            if (manifest.Notes.Count == 0)
                output("  (바뀐 점 없음: --notes <파일> 로 한 줄에 하나씩 적을 수 있어요)");
            return 0;
        }
        catch (InvalidOperationException e)
        {
            output(e.Message);
            return 1;
        }
    }

    private static async Task<int> CheckAsync(Func<string, string?> option, Action<string> output)
    {
        var source = UpdateSource.Find(name => name == UpdateSource.UrlVariable && option("--base") is { } b ? b : Environment.GetEnvironmentVariable(name));
        if (source is null)
        {
            output("업데이트 주소가 없어요 (Directory.Build.props 의 TopstUpdateUrl, 또는 --base).");
            return 2;
        }
        var current = UpdateVersion.Parse(option("--current") ?? "0.0.0");
        var checker = new UpdateChecker(source);
        var result = await checker.CheckAsync(current);
        output($"주소 {source.ManifestUrl}");
        if (result.Problem is { } problem)
        {
            output($"문제: {problem} · {result.Detail}");
            return 1;
        }
        var m = result.Manifest!;
        output($"버전 {m.Version} ({m.Released}) · {(result.IsNewer ? "새 버전" : "새 버전 아님")} · 서명 확인됨");
        output($"설치 파일 {m.Installer.Url} · {m.Installer.Size:N0} B · {m.Installer.Sha256}");
        foreach (var note in m.Notes)
            output("  · " + note);
        return 0;
    }
}
