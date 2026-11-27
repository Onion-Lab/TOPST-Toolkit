using System.Diagnostics;
using System.Text;

namespace TopstToolkit.Core.Board;

/// <summary>
/// "처음 연결 — 비밀번호 입력" (design §5.10): puts this PC's SSH key on the board with the board's password, once, the way
/// ssh-copy-id does; from then on the app logs in with the key. The password goes to ssh through SSH_ASKPASS (the app
/// itself answers, see <see cref="AskpassVariable"/>); it is not stored anywhere.
/// </summary>
public static class BoardKeys
{
    /// <summary>
    /// Set on the ssh process only. ssh starts the askpass program (the app's own exe) with this in its environment; the
    /// app, started that way, prints it and exits before anything else happens (App.OnStartup).
    /// </summary>
    public const string AskpassVariable = "TOPST_TOOLKIT_ASKPASS";

    private static readonly string[] KeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    public static string SshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>The public half of the PC's first SSH key (what the board keeps), or null when the PC has none.</summary>
    public static string? PublicKey()
    {
        foreach (var name in KeyNames)
        {
            var file = Path.Combine(SshDir, name + ".pub");
            if (File.Exists(file) && File.Exists(Path.Combine(SshDir, name)) && File.ReadAllText(file).Trim() is { Length: > 0 } key)
                return key;
        }
        return null;
    }

    /// <summary>The PC's public key; makes one (ed25519, no passphrase) with Windows' ssh-keygen when there is none.</summary>
    public static async Task<string> EnsureKeyAsync(string sshExe, CancellationToken ct = default)
    {
        if (PublicKey() is { } existing)
            return existing;
        Directory.CreateDirectory(SshDir);
        var keygen = Path.Combine(Path.GetDirectoryName(sshExe)!, "ssh-keygen.exe");
        var psi = new ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "topst-toolkit", "-f", Path.Combine(SshDir, "id_ed25519") })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return PublicKey() ?? throw new BoardException(BoardFailure.Command, "이 PC의 키를 만들지 못했어요", (await error.ConfigureAwait(false)).Trim());
    }

    /// <summary>The board's shell command that adds <paramref name="publicKey"/> to root's authorized_keys once.</summary>
    public static string AddKeyCommand(string publicKey)
    {
        var key = SshClient.Quote(publicKey.Trim());
        return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
               $"(grep -qxF {key} ~/.ssh/authorized_keys || echo {key} >> ~/.ssh/authorized_keys)";
    }

    /// <summary>
    /// Logs in once with <paramref name="password"/> and leaves the PC's key on the board. Then checks that the key works.
    /// </summary>
    /// <param name="askpassExe">The program ssh asks for the password: the app's own exe.</param>
    public static async Task RegisterAsync(BoardTarget target, string password, string askpassExe, CancellationToken ct = default)
    {
        var ssh = new SshClient(target);
        var key = await EnsureKeyAsync(ssh.SshPath, ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(ssh.SshPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
                 {
                     "-o", "BatchMode=no", "-o", "PubkeyAuthentication=no", "-o", "PreferredAuthentications=password,keyboard-interactive",
                     "-o", "NumberOfPasswordPrompts=1", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=8", "-o", "LogLevel=ERROR",
                     "-p", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), target.Address, AddKeyCommand(key),
                 })
            psi.ArgumentList.Add(arg);
        psi.Environment["SSH_ASKPASS"] = askpassExe;
        psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        psi.Environment["DISPLAY"] = "localhost:0";
        psi.Environment[AskpassVariable] = password;
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new BoardException(BoardFailure.Unreachable, "보드가 제때 답하지 않았어요");
        }
        var text = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode == 255)
        {
            var failure = SshClient.Classify(text);
            throw failure.Failure == BoardFailure.KeyRefused
                ? new BoardException(BoardFailure.KeyRefused, "비밀번호가 맞지 않아요", text)
                : failure;
        }
        if (process.ExitCode != 0)
            throw new BoardException(BoardFailure.Command, "보드에 이 PC의 키를 넣지 못했어요", text);
        // from now on the key has to work on its own
        await ssh.RunCheckedAsync("true", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
    }
}
