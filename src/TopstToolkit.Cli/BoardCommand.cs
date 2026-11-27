using System.Globalization;
using TopstToolkit.Core.Board;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;

namespace TopstToolkit.Cli;

/// <summary>
/// The board (design §5.10) from the console, the same calls the 보드 screen makes:
///
///   board status   [--host 192.168.0.100] [--user root]   installs the runner when needed, prints the status twice
///   board models
///   board deploy   &lt;project id&gt;
///   board run      &lt;project id&gt; &lt;picture&gt;... [--npu 0|1]
///   board video    &lt;project id&gt; &lt;video&gt; [--npu 0|1] [--every 1] [--out &lt;folder&gt;]
///   board bench    &lt;project id&gt; [--npu 0|1] [--count 100]
/// </summary>
internal static class BoardCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<string, string?> option, EngineLayout layout, string workspaceDir, Action<string> output)
    {
        var target = new BoardTarget(option("--host") ?? "192.168.0.100", option("--user") ?? "root");
        var ssh = new SshClient(target);
        var agent = new BoardAgent(ssh, BoardAgent.RunnerScriptIn(layout.HelpersDir));
        var store = new ProjectStore(new WorkspaceRoot(workspaceDir));
        var npu = int.TryParse(option("--npu"), out var n) ? n : 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            var installed = await agent.EnsureRunnerAsync(cts.Token);
            output($"보드 {target.Address} · 실행기 {agent.RunnerVersion} {(installed ? "설치함" : "있음 (건너뜀)")}");
            switch (args.ElementAtOrDefault(0))
            {
                case "status":
                {
                    using var monitor = ssh.Start($"{BoardAgent.RunnerPath} monitor --interval 1 --count 2");
                    while (await monitor.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                        if (BoardStatus.Parse(line) is { } s)
                            output($"{s.Host} · {s.OsShort} · CPU {s.CpuAll:0}% [{string.Join(' ', s.Cpu.Select(c => c.ToString("0", CultureInfo.InvariantCulture)))}] · " +
                                   $"메모리 {s.MemoryUsedPercent:0}% · NPU용 {BoardAgent.Mb(s.NpuMemory.Free)} 남음 · 온도 {string.Join('/', s.Temperatures.Select(t => t.Celsius))} · " +
                                   $"NPU {string.Join(' ', s.Npus.Select(p => (p.ClockOn ? "on" : "off") + $"/{p.Busy:0}%"))} · 저장 {BoardAgent.Mb(s.Disk.Free)} · {s.Network.Mbps} Mbps");
                    return 0;
                }
                case "models":
                    foreach (var m in await agent.ListModelsAsync(cts.Token))
                        output($"{(m.ByApp ? "앱" : "기본")} {m.Name,-32} {m.Type,-6} {string.Join('x', m.Input)} {BoardAgent.Mb(m.Bytes)} {m.Manifest?.Model}");
                    return 0;
                case "deploy" or "run" or "video" or "bench":
                    break;
                default:
                    output("usage: board status|models|deploy|run|video|bench ...");
                    return 2;
            }

            if (store.Load(args.ElementAtOrDefault(1) ?? "") is not { } project)
            {
                output("프로젝트를 찾지 못했어요: " + args.ElementAtOrDefault(1));
                return 2;
            }
            var title = project.Zoo?.Display ?? Path.GetFileNameWithoutExtension(project.Model?.Source ?? project.Name);
            if (BoardProjects.Deployment(store, project, title, layout) is not { } deployment)
            {
                output("빌드한 결과(net.so 포함)가 없어요. 먼저 빌드해 주세요.");
                return 1;
            }
            var deployed = await agent.DeployAsync(deployment, cts.Token);
            output($"올림 {deployed.RemoteDir} · {(deployed.Skipped ? "같은 모델이 있어 건너뜀" : BoardAgent.Mb(deployed.Bytes))}");
            if (args[0] == "deploy")
                return 0;
            if (args[0] == "bench")
            {
                var bench = await agent.BenchAsync(deployed.RemoteDir, npu, int.TryParse(option("--count"), out var c) ? c : 100, cts.Token);
                output(bench.Error is not null ? "실패: " + bench.Error
                    : $"NPU {bench.Npu} · {bench.Count}번 · 평균 {bench.Average:0.000} ms · 최소 {bench.Min:0.000} · 최대 {bench.Max:0.000} · 가운데 {bench.Median:0.000}");
                return bench.Error is null ? 0 : 1;
            }

            var names = BoardProjects.ClassNames(store, project, layout);
            var runner = new BoardRunner(ssh, layout);
            var letterbox = project.Options.Convert.Letterbox;
            if (args[0] == "run")
            {
                var pictures = args.Skip(2).Select(Path.GetFullPath).ToList();
                var summary = await runner.RunAsync(deployed.RemoteDir, npu, letterbox, pictures, null, 1, frame =>
                {
                    var r = frame.Result;
                    var what = r.ClassId is { } cls ? BoardProjects.NameOf(names, "class", cls)
                        : string.Join(", ", frame.Boxes.Select(b => $"{BoardProjects.NameOf(names, "obj", b.ClassNumber)} {b.Score * 100:0.0}% [{b.X1:0},{b.Y1:0},{b.X2:0},{b.Y2:0}]"));
                    output($"  {Path.GetFileName(frame.Info.Source)} · NPU {r.NpuMs:0.000} ms · 후처리 {r.PostMs:0.000} ms · {what}");
                }, ct: cts.Token);
                output($"사진 {summary.Frames}장 · NPU {npu} · 평균 {summary.AverageNpuMs:0.000} ms · 초당 {summary.FramesPerSecond:0.0}장 · 입력 {summary.Width}x{summary.Height} · {summary.Type}");
                return 0;
            }

            // video
            var video = Path.GetFullPath(args.ElementAtOrDefault(2) ?? "");
            var every = int.TryParse(option("--every"), out var e) ? Math.Max(1, e) : 1;
            var outDir = Path.GetFullPath(option("--out") ?? Path.Combine(Path.GetDirectoryName(video)!, "board-run"));
            Directory.CreateDirectory(outDir);
            var frames = new List<BoardRunFrame>();
            VideoInfo? info = null;
            var last = DateTime.MinValue;
            var result = await runner.RunAsync(deployed.RemoteDir, npu, letterbox, null, video, every, frame =>
            {
                frames.Add(frame);
                if ((DateTime.Now - last).TotalSeconds >= 2)
                {
                    last = DateTime.Now;
                    output($"  장면 {frame.Info.Index}/{info?.Frames} · NPU {frame.Result.NpuMs:0.0} ms · 검출 {frame.Boxes.Count}개");
                }
            }, v => info = v, cts.Token);
            output($"장면 {result.Frames}개 · NPU {npu} · 평균 {result.AverageNpuMs:0.000} ms · 초당 {result.FramesPerSecond:0.0}장 · {result.Elapsed.TotalSeconds:0.0} s");
            var stem = Path.GetFileNameWithoutExtension(video);
            var resultsPath = Path.Combine(outDir, stem + ".board.jsonl");
            BoardVideoOutput.WriteResults(resultsPath, frames);
            BoardVideoOutput.WriteCsv(Path.Combine(outDir, stem + ".board.csv"), frames, cls => BoardProjects.NameOf(names, result.Type, cls));
            var labels = frames.SelectMany(f => f.Boxes).Select(b => b.ClassNumber).Distinct().ToDictionary(cls => cls, cls => BoardProjects.NameOf(names, result.Type, cls));
            var font = BoardVideoOutput.KoreanFont() ?? throw new FileNotFoundException("no Korean font in Windows\\Fonts");
            var outVideo = Path.Combine(outDir, stem + ".board.mp4");
            await BoardVideoOutput.WriteVideoAsync(layout, video, resultsPath, labels, font, outVideo, ct: cts.Token);
            output($"결과 영상 {outVideo} ({new FileInfo(outVideo).Length / 1048576.0:0.0} MB) · 표 {stem}.board.csv");
            return 0;
        }
        catch (BoardException ex)
        {
            output($"보드: {ex.Message} ({ex.Failure}) {ex.Detail}");
            return 1;
        }
    }
}
