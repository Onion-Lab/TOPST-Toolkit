using System.Diagnostics;
using System.Globalization;

namespace TopstToolkit.Core.Components;

/// <summary>
/// Several components in one go (design D12, 2026-11-25): everything they download starts at once
/// (<see cref="ComponentInstaller.PrefetchAsync"/>), then the installs — one after another for the app's install screen,
/// which shows one component at a time, or side by side for Setup (<c>together</c>): the engine and the board tools at
/// the same time, the model zoo's packages after the engine whose Python they use.
/// </summary>
public static class ComponentBatch
{
    /// <summary>The components of <paramref name="requested"/> (or of the whole catalog) that are missing, broken or older than the catalog.</summary>
    public static IReadOnlyList<string> Needed(ComponentInstaller installer, IEnumerable<string>? requested = null) =>
        [.. installer.Catalog.Components.Select(c => c.Id).Where(id => requested is null || requested.Contains(id)).Where(id => !installer.IsCurrent(id))];

    /// <param name="ids">In the catalog's order: a component comes after the one it needs.</param>
    /// <param name="together">Side by side, and a failure stops only what needs the failed component (Setup). Otherwise
    /// one after another, stopping at the first failure or cancel (the app's screen).</param>
    /// <param name="started">Told when the install of a component begins (from the installing thread).</param>
    /// <returns>A result for every component that was tried; one after another, the ones after a failure are not.</returns>
    public static async Task<IReadOnlyList<ComponentInstallResult>> RunAsync(ComponentInstaller installer, IReadOnlyList<string> ids, bool together,
        Action<string>? started, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prefetch = installer.PrefetchAsync(ids, progress, stop.Token);
        try
        {
            if (!together)
            {
                var results = new List<ComponentInstallResult>();
                foreach (var id in ids)
                {
                    started?.Invoke(id);
                    var result = await installer.InstallAsync(id, progress, stop.Token).ConfigureAwait(false);
                    results.Add(result);
                    if (!result.Success)
                        break;
                }
                return results;
            }

            var tasks = new Dictionary<string, Task<ComponentInstallResult>>();
            foreach (var id in ids)
            {
                var needs = installer.Catalog.Get(id).Needs;
                tasks[id] = needs is not null && tasks.TryGetValue(needs, out var first) ? AfterAsync(first, id) : StartAsync(id);
            }
            return await Task.WhenAll(tasks.Values).ConfigureAwait(false);

            async Task<ComponentInstallResult> StartAsync(string id)
            {
                await Task.Yield();
                started?.Invoke(id);
                return await installer.InstallAsync(id, progress, stop.Token).ConfigureAwait(false);
            }

            async Task<ComponentInstallResult> AfterAsync(Task<ComponentInstallResult> first, string id)
            {
                var before = await first.ConfigureAwait(false);
                if (before.Success)
                    return await StartAsync(id).ConfigureAwait(false);
                return new ComponentInstallResult
                {
                    Component = id, Cancelled = before.Cancelled, LogDirectory = before.LogDirectory,
                    Error = before.Cancelled ? "cancelled" : $"not installed: it needs {before.Component}, which could not be installed",
                };
            }
        }
        finally
        {
            // downloads nobody waits for any more (an install stopped) end here and keep what they got
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await prefetch.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }
}

/// <summary>
/// How far a run of several components is, from their progress reports (Setup's page, the elevated helper): every line of
/// every component's plan (<see cref="ComponentSteps"/>), the overall fraction, the bytes still coming in and how fast,
/// and the newest step in words. Thread-safe.
/// </summary>
public sealed class BatchProgress
{
    private readonly object _gate = new();
    private readonly List<string> _ids;
    private readonly List<string> _titles;
    private readonly IReadOnlyList<IReadOnlyList<InstallStepPlan>> _plans;
    private readonly double[][] _fractions;
    private readonly Dictionary<(int Component, string Key), (long Done, long Total)> _downloads = [];
    private readonly Queue<(long At, long Bytes)> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>The newest step of each component and its order of arrival; whether its install got to its end.</summary>
    private readonly (string Step, long At)?[] _last;
    private readonly bool[] _ended;
    private long _sequence;

    public BatchProgress(ComponentCatalog catalog, IReadOnlyList<string> ids)
    {
        _ids = [.. ids];
        _titles = [.. ids.Select(id => catalog.Get(id).Title)];
        _plans = [.. ids.Select(id => ComponentSteps.For(catalog.Get(id)))];
        _fractions = [.. _plans.Select(p => new double[p.Count])];
        _last = new (string, long)?[_ids.Count];
        _ended = new bool[_ids.Count];
    }

    public void Report(ComponentProgress p)
    {
        lock (_gate)
        {
            var c = _ids.IndexOf(p.Component);
            if (c < 0)
                return;
            var line = ComponentSteps.IndexOf(_plans[c], p.Key);
            if (line < 0)
                return;
            if (!p.Ahead)
            {
                // the install is at this line: the lines before it are done
                for (var l = 0; l < line; l++)
                    _fractions[c][l] = 1;
                _last[c] = (_titles[c] + " · " + p.Step, ++_sequence);
                if (p.Key == "finish")
                    _ended[c] = true;
            }
            if (p.Fraction is { } f)
                _fractions[c][line] = Math.Max(_fractions[c][line], f * ComponentSteps.BytesShare(_plans[c][line]));
            if (p.Total is > 0 && p.Done is { } done && (p.IsDownload || p.Key is "packages" or "extras"))
                _downloads[(c, p.Key)] = (done, p.Total.Value);
        }
    }

    /// <summary>A component is in place (or will not be): all its lines count as done for the bar.</summary>
    public void Finished(string id, bool success)
    {
        lock (_gate)
        {
            var c = _ids.IndexOf(id);
            if (c < 0)
                return;
            if (success)
                Array.Fill(_fractions[c], 1);
            _ended[c] = true;
            foreach (var key in _downloads.Keys.Where(k => k.Component == c).ToList())
                _downloads[key] = (_downloads[key].Total, _downloads[key].Total);
        }
    }

    public double Overall
    {
        get
        {
            lock (_gate)
                return ComponentSteps.Overall(_plans, (c, l) => _fractions[c][l]);
        }
    }

    /// <summary>
    /// "엔진 · Python 패키지 설치하는 중": the newest step of an install that still runs (when the board tools are done,
    /// their last words do not stay up while the engine works on); the newest of all once every install ended.
    /// </summary>
    public string Headline
    {
        get
        {
            lock (_gate)
            {
                var running = Enumerable.Range(0, _ids.Count).Where(c => !_ended[c] && _last[c] is not null).ToList();
                var pick = (running.Count > 0 ? running : Enumerable.Range(0, _ids.Count).Where(c => _last[c] is not null))
                    .OrderByDescending(c => _last[c]!.Value.At).Cast<int?>().FirstOrDefault();
                return pick is { } c ? _last[c]!.Value.Step : "";
            }
        }
    }

    /// <summary>"받는 중 812 MB / 1.9 GB · 24 MB/s · 남은 시간 약 1분" while something still downloads; empty otherwise.</summary>
    public string DownloadLine
    {
        get
        {
            lock (_gate)
            {
                var done = _downloads.Values.Sum(d => d.Done);
                var total = _downloads.Values.Sum(d => d.Total);
                var now = _clock.ElapsedMilliseconds;
                _samples.Enqueue((now, done));
                while (_samples.Count > 2 && now - _samples.Peek().At > 8000)
                    _samples.Dequeue();
                if (total <= 0 || done >= total)
                    return "";
                var text = $"받는 중 {ComponentSteps.Size(done)} / {ComponentSteps.Size(total)}";
                var (at, bytes) = _samples.Peek();
                var seconds = (now - at) / 1000.0;
                if (seconds >= 2 && done > bytes)
                {
                    var rate = (done - bytes) / seconds;
                    var left = (total - done) / rate;
                    text += $" · {Rate(rate)} · 남은 시간 약 " + (left < 60 ? $"{Math.Max(5, Math.Round(left / 5) * 5):0}초" : $"{Math.Ceiling(left / 60):0}분");
                }
                return text;
            }
        }
    }

    private static string Rate(double bytesPerSecond) => bytesPerSecond >= 1 << 20
        ? (bytesPerSecond / (1 << 20)).ToString(bytesPerSecond >= 10 << 20 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB/s"
        : Math.Max(1, bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
}
