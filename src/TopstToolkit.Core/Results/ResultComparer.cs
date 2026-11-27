using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Results;

/// <summary>One object as the board network (INT8) and the original network (FP32) see it. Either side may be missing.</summary>
public sealed record DetectionMatch(Detection? Int8, Detection? Fp32, double Iou)
{
    /// <summary>INT8 score minus FP32 score, in percent points; null when one side did not find the object.</summary>
    public double? ScoreDelta => Int8 is not null && Fp32 is not null ? Int8.ScorePercent - Fp32.ScorePercent : null;

    public Detection Any => Int8 ?? Fp32!;
}

/// <summary>Pairs the boxes of two results of the same picture so the screen can show what quantization changed (design §5.5).</summary>
public static class ResultComparer
{
    /// <summary>
    /// Pairs boxes of the same class by overlap, best overlap first. Boxes without a partner come back alone:
    /// only in INT8 (newly found) or only in FP32 (lost by quantization). INT8 rows keep their order, lost ones follow.
    /// </summary>
    public static IReadOnlyList<DetectionMatch> Match(IReadOnlyList<Detection> int8, IReadOnlyList<Detection> fp32, double minIou = 0.45)
    {
        var pairs = new List<(int I, int F, double Iou)>();
        for (var i = 0; i < int8.Count; i++)
            for (var f = 0; f < fp32.Count; f++)
                if (int8[i].ClassNumber == fp32[f].ClassNumber && Iou(int8[i], fp32[f]) is var iou && iou >= minIou)
                    pairs.Add((i, f, iou));

        var partnerOfInt8 = new Dictionary<int, (int F, double Iou)>();
        var usedFp32 = new HashSet<int>();
        foreach (var (i, f, iou) in pairs.OrderByDescending(p => p.Iou))
        {
            if (partnerOfInt8.ContainsKey(i) || !usedFp32.Add(f))
                continue;
            partnerOfInt8[i] = (f, iou);
        }

        var result = new List<DetectionMatch>();
        for (var i = 0; i < int8.Count; i++)
            result.Add(partnerOfInt8.TryGetValue(i, out var p) ? new DetectionMatch(int8[i], fp32[p.F], p.Iou) : new DetectionMatch(int8[i], null, 0));
        for (var f = 0; f < fp32.Count; f++)
            if (!usedFp32.Contains(f))
                result.Add(new DetectionMatch(null, fp32[f], 0));
        return result;
    }

    /// <summary>Intersection over union of two boxes, 0..1.</summary>
    public static double Iou(Detection a, Detection b)
    {
        var w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        var h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0)
            return 0;
        var intersection = w * h;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
