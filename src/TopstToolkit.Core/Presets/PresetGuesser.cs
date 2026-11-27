using System.Globalization;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.Core.Presets;

/// <param name="PresetId">The family that fits the model best.</param>
/// <param name="Reason">One plain sentence shown to the user: why this family.</param>
/// <param name="Notes">Things the user should check.</param>
/// <param name="NumClass">Class count read from the output shapes, when it can be read.</param>
/// <param name="OutputOrder">converter <c>--output-order</c> when the tensor order differs from the preset.</param>
/// <param name="RawExport">
/// The file is a YOLO model as the training tool exports it, with the box maths still inside:
/// <c>yolo-v8</c> (one output [1, 4 + classes, anchors]; the app can prepare it) or <c>yolo-v5</c>
/// (one output [1, anchors, 5 + classes]; not prepared automatically yet).
/// </param>
public sealed record PresetGuess(string PresetId, string Reason, IReadOnlyList<string> Notes, int? NumClass = null, string? OutputOrder = null,
    string? RawExport = null);

/// <summary>
/// Guesses the model family from the file type and the output shapes found by model inspection
/// (design §4.7). The rules follow the six verified sample models; anything else falls back to "기타".
/// </summary>
public static class PresetGuesser
{
    private const string BoxMathsInsideSsd = "박스 계산이 모델 안에 들어 있어요. NPU가 지원하지 않는 연산이 있으면 그 앞에서 잘라내야 합니다 (빌드에서 알려 드려요).";
    private const string BoxMathsInsideYolo = "박스를 계산하는 부분이 모델 안에 들어 있어, 그 부분을 떼어내는 준비가 필요해요.";

    /// <summary>
    /// The note says "the box maths is inside the model and may have to be cut off". The check that runs when a model is
    /// brought in (design §5.7) answers that precisely, so screens that show the check leave these notes out.
    /// </summary>
    public static bool IsAboutBoxMathsInside(string note) => note is BoxMathsInsideSsd or BoxMathsInsideYolo;

    public static PresetGuess Guess(string format, ModelFacts? facts)
    {
        if (format == "tflite")
            return new("tflite-ssd", "TFLite 모델이라 후처리가 들어 있는 SSD로 보았어요.", ["분류 모델이라면 작업 종류를 분류로 바꿔 주세요."]);
        if (format == "darknet")
            return new("darknet-yolo", "Darknet 설정 파일(.cfg)이라 Darknet YOLO로 보았어요.", []);
        if (facts is null || facts.Outputs.Count == 0)
            return new("generic-unknown", "모델 구조를 읽지 못해 기타로 두었어요.", ["작업 종류를 직접 골라 주세요."]);

        var shapes = facts.Outputs.Select(o => o.Shape.Select(ParseDim).ToArray()).ToList();
        var names = string.Join(" ", facts.Outputs.Select(o => o.Name));

        // anchor-based YOLO (v3 / v5 / v7): 3 or 4 heads [1, 3 × (classes + 5), H, W]
        if (shapes.Count is 3 or 4 && shapes.All(s => s.Length == 4) && shapes.Select(s => s[1]).Distinct().Count() == 1
            && shapes[0][1] is > 15 and var c && c % 3 == 0)
        {
            var classes = c / 3 - 5;
            var (version, sure) = names switch
            {
                _ when names.Contains("model.105/") || names.Contains("model.121/") || names.Contains("/model/model.") => ("v7", true),
                _ when names.Contains("model.24/") || names.Contains("model.33/") => ("v5", true),
                _ when names.Contains("model.28/") => ("v3", true),
                _ => ("v5", false),
            };
            var notes = new List<string>();
            if (!sure) notes.Add("YOLOv3 · v5 · v7은 출력 모양이 같아서 이름으로 구분하지 못했어요. 버전을 확인해 주세요.");
            return new("pytorch-yolo-" + version, $"출력이 {shapes.Count}개이고 채널이 {c}개(3 × ({classes} + 5))라 YOLO{version} · {classes}클래스로 보여요.", notes, classes);
        }

        // anchor-free YOLO v8 / v6: pairs of [1, classes, H, W] and [1, 4 × reg, H, W]
        if (shapes.Count == 6 && shapes.All(s => s.Length == 4))
        {
            var a = shapes[0][1];
            var b = shapes[1][1];
            var reg = a is 64 or 68 ? a : b is 64 or 68 ? b : null;
            if (reg is not null && a != b)
            {
                var classes = reg == a ? b : a;
                var order = reg == a ? "lc" : "cl";
                var version = reg == 64 ? "v8" : "v6";
                return new("pytorch-yolo-" + version, $"출력이 6개(클래스 {classes}채널 + 박스 {reg}채널 × 3)라 YOLO{version}로 보여요.", [], classes, order);
            }
        }

        // YOLOX: (classes, 4, 1) × 3
        if (shapes.Count == 9 && shapes.All(s => s.Length == 4))
        {
            var triple = shapes.Take(3).Select(s => s[1]).ToArray();
            if (triple.Contains(4) && triple.Contains(1) && triple.Any(x => x > 4))
            {
                var classes = triple.First(x => x > 4);
                var order = string.Concat(triple.Select(x => x == 4 ? 'l' : x == 1 ? 'o' : 'c'));
                return new("pytorch-yolo-x", $"출력이 9개(클래스 {classes} · 박스 4 · 물체 여부 1 × 3)라 YOLOX로 보여요.", [], classes, order);
            }
        }

        // SSD exported with box decoding inside: [1, N, classes + 1] and [1, N, 4]
        if (shapes.Count == 2 && shapes.All(s => s.Length == 3) && shapes.Any(s => s[2] == 4))
        {
            var scores = shapes.FirstOrDefault(s => s[2] != 4) ?? shapes[0];
            int? classes = scores[2] is > 1 and var k ? k - 1 : null; // one of them is the background class
            return new("ssd-anchor", "점수와 박스가 따로 나오는 출력 2개라 SSD로 보여요.", [BoxMathsInsideSsd], classes);
        }

        // a YOLO model straight from the exporter: the box maths is still inside and has to be cut off first
        if (shapes.Count == 1 && shapes[0].Length == 3 && shapes[0][1] is { } d1 && shapes[0][2] is { } d2)
        {
            if (d2 > d1 && d1 > 4)
                return new("generic-unknown", $"YOLOv8 계열에서 내보낸 원본 모델이에요 (출력 [{d1}, {d2}] 하나, 클래스 {d1 - 4}개).",
                    [BoxMathsInsideYolo], d1 - 4, RawExport: "yolo-v8");
            if (d1 > d2 && d2 > 5 && d1 >= 1000)
                return new("generic-unknown", $"YOLOv5 · v7 계열에서 내보낸 원본 모델로 보여요 (출력 [{d1}, {d2}] 하나).",
                    ["이 형태는 아직 앱이 자동으로 준비하지 못해요. 모델 모음의 YOLOv5u는 앱이 바로 준비합니다. 툴킷의 YOLO 준비 스크립트로 만든 모델이 있다면 함께 만든 .bin 파일과 같은 폴더에 두고 가져오세요."],
                    d2 - 5, RawExport: "yolo-v5");
        }

        // classification: one [N, classes] output
        if (shapes.Count == 1 && shapes[0].Length == 2 && shapes[0][1] is > 1 and var n)
        {
            var notes = n == 1000 ? new List<string>() : ["ImageNet 1000클래스가 아니에요. 라벨 파일을 직접 넣어 주세요."];
            return new("classification-imagenet", $"출력이 [{n}] 하나라 {n}클래스 분류 모델로 보여요.", notes, n);
        }

        return new("generic-unknown", "알려진 계열과 맞지 않아 기타로 두었어요.", ["작업 종류를 직접 골라 주세요."]);
    }

    private static int? ParseDim(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
