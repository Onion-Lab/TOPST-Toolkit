namespace TopstToolkit.Core.Presets;

/// <summary>
/// Korean display names for the classes of the bundled label sets (COCO 80 / COCO 91 / VOC 20). The toolkit only
/// accepts ASCII labels, so the app keeps the English label for the toolkit and draws the Korean name itself
/// (design §5.5). ImageNet's 1000 classes stay in English.
/// </summary>
public static class LabelNames
{
    private static readonly Dictionary<string, string> Korean = new(StringComparer.Ordinal)
    {
        ["person"] = "사람", ["bicycle"] = "자전거", ["car"] = "자동차", ["motorcycle"] = "오토바이", ["motorbike"] = "오토바이",
        ["airplane"] = "비행기", ["aeroplane"] = "비행기", ["bus"] = "버스", ["train"] = "기차", ["truck"] = "트럭", ["boat"] = "보트",
        ["trafficlight"] = "신호등", ["firehydrant"] = "소화전", ["streetsign"] = "도로 표지판", ["stopsign"] = "정지 표지판",
        ["parkingmeter"] = "주차 요금기", ["bench"] = "벤치", ["bird"] = "새", ["cat"] = "고양이", ["dog"] = "개", ["horse"] = "말",
        ["sheep"] = "양", ["cow"] = "소", ["elephant"] = "코끼리", ["bear"] = "곰", ["zebra"] = "얼룩말", ["giraffe"] = "기린",
        ["hat"] = "모자", ["backpack"] = "배낭", ["umbrella"] = "우산", ["shoe"] = "신발", ["eyeglasses"] = "안경", ["handbag"] = "핸드백",
        ["tie"] = "넥타이", ["suitcase"] = "여행 가방", ["frisbee"] = "프리스비", ["skis"] = "스키", ["snowboard"] = "스노보드",
        ["sportsball"] = "공", ["kite"] = "연", ["baseballbat"] = "야구 방망이", ["baseballglove"] = "야구 글러브",
        ["skateboard"] = "스케이트보드", ["surfboard"] = "서프보드", ["tennisracket"] = "테니스 라켓", ["bottle"] = "병",
        ["plate"] = "접시", ["wineglass"] = "와인잔", ["cup"] = "컵", ["fork"] = "포크", ["knife"] = "나이프", ["spoon"] = "숟가락",
        ["bowl"] = "그릇", ["banana"] = "바나나", ["apple"] = "사과", ["sandwich"] = "샌드위치", ["orange"] = "오렌지",
        ["broccoli"] = "브로콜리", ["carrot"] = "당근", ["hotdog"] = "핫도그", ["pizza"] = "피자", ["donut"] = "도넛", ["cake"] = "케이크",
        ["chair"] = "의자", ["couch"] = "소파", ["sofa"] = "소파", ["pottedplant"] = "화분", ["bed"] = "침대", ["mirror"] = "거울",
        ["diningtable"] = "식탁", ["window"] = "창문", ["desk"] = "책상", ["toilet"] = "변기", ["door"] = "문", ["tv"] = "TV",
        ["tvmonitor"] = "TV", ["laptop"] = "노트북", ["mouse"] = "마우스", ["remote"] = "리모컨", ["keyboard"] = "키보드",
        ["cellphone"] = "휴대전화", ["microwave"] = "전자레인지", ["oven"] = "오븐", ["toaster"] = "토스터", ["sink"] = "싱크대",
        ["refrigerator"] = "냉장고", ["blender"] = "믹서기", ["book"] = "책", ["clock"] = "시계", ["vase"] = "꽃병",
        ["scissors"] = "가위", ["teddybear"] = "곰 인형", ["hairdrier"] = "헤어드라이어", ["hairdryer"] = "헤어드라이어",
        ["toothbrush"] = "칫솔", ["hairbrush"] = "머리빗",
    };

    /// <summary>The Korean name when the label is a known class name, otherwise the label as it is.</summary>
    public static string Display(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return label;
        // "ImageNet style" labels list synonyms: "malamute, malemute, Alaskan malamute" → show the first
        var first = label.Split(',')[0].Trim();
        return Korean.TryGetValue(KeyOf(first), out var korean) ? korean : first;
    }

    public static bool HasKorean(string label) => Korean.ContainsKey(KeyOf(label.Split(',')[0]));

    private static string KeyOf(string label) =>
        new(label.Where(c => c is not (' ' or '_' or '-')).Select(char.ToLowerInvariant).ToArray());
}
