using System.Numerics;

namespace TCRankingViewer;

/// <summary>
/// 排名視窗共用的色彩與小工具，供 MainWindow / PlayerHistoryWindow /（未來）PartyFinderWindow
/// 共用同一套配色與計算，確保三個視窗排版一致（見 CLAUDE.md：避免重複實作各自漂移）。
/// </summary>
public static class RankColors
{
    public static readonly Vector4 Gold   = new(1.00f, 0.85f, 0.30f, 1f);
    public static readonly Vector4 Silver = new(0.80f, 0.80f, 0.85f, 1f);
    public static readonly Vector4 Bronze = new(0.80f, 0.55f, 0.30f, 1f);
    public static readonly Vector4 Green  = new(0.45f, 0.90f, 0.45f, 1f);
    public static readonly Vector4 Red    = new(0.95f, 0.40f, 0.40f, 1f);
    public static readonly Vector4 Dim    = new(0.55f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 White  = new(1.00f, 1.00f, 1.00f, 1f);
    public static readonly Vector4 Teal   = new(0.40f, 0.85f, 0.85f, 1f);
    public static readonly Vector4 Purple = new(0.78f, 0.58f, 0.96f, 1f);
    public static readonly Vector4 Blue   = new(0.44f, 0.66f, 0.86f, 1f);
    public static readonly Vector4 Amber  = new(1.00f, 0.73f, 0.24f, 1f);

    public static readonly Dictionary<string, Vector4> JobColors =
        new(StringComparer.Ordinal)
    {
        { "騎士", new(0.65f,0.83f,1.00f,1) }, { "戰士", new(0.94f,0.35f,0.35f,1) },
        { "暗騎", new(0.82f,0.31f,0.75f,1) }, { "絕槍", new(0.75f,0.75f,0.50f,1) },
        { "白魔", new(0.95f,0.95f,0.95f,1) }, { "學者", new(0.58f,0.71f,0.96f,1) },
        { "占星", new(0.97f,0.87f,0.48f,1) }, { "賢者", new(0.54f,0.84f,0.80f,1) },
        { "武僧", new(0.96f,0.62f,0.35f,1) }, { "龍騎", new(0.40f,0.57f,0.87f,1) },
        { "忍者", new(0.96f,0.36f,0.55f,1) }, { "武士", new(0.89f,0.51f,0.18f,1) },
        { "鐮魂", new(0.63f,0.44f,0.70f,1) }, { "蝰蛇", new(0.35f,0.80f,0.45f,1) },
        { "詩人", new(0.90f,0.86f,0.62f,1) }, { "機工", new(0.64f,0.87f,0.87f,1) },
        { "舞者", new(0.93f,0.62f,0.75f,1) },
        { "黑魔", new(0.65f,0.55f,0.90f,1) }, { "召喚", new(0.45f,0.78f,0.62f,1) },
        { "赤魔", new(0.90f,0.50f,0.50f,1) }, { "繪靈", new(0.85f,0.65f,0.90f,1) },
    };

    // 名次配色：金＝前 3、白＝前 10、銅＝前 50、其餘淡灰。
    public static Vector4 RankColor(int rank) => rank switch
    {
        <= 3  => Gold,
        <= 10 => White,
        <= 50 => Bronze,
        _     => Dim,
    };

    // ── FFLogs parse 百分位色盤（沿用 FFLogs 官方色碼）──────────────────────────
    // Grey <25 / Green 25–49 / Blue 50–74 / Purple 75–94 / Orange 95–98 / Pink 99 / Gold 100
    public static readonly Vector4 FlGrey   = new(0x66/255f, 0x66/255f, 0x66/255f, 1f);
    public static readonly Vector4 FlGreen  = new(0x1e/255f, 0xff/255f, 0x00/255f, 1f);
    public static readonly Vector4 FlBlue   = new(0x00/255f, 0x70/255f, 0xff/255f, 1f);
    public static readonly Vector4 FlPurple = new(0xa3/255f, 0x35/255f, 0xee/255f, 1f);
    public static readonly Vector4 FlOrange = new(0xff/255f, 0x80/255f, 0x00/255f, 1f);
    public static readonly Vector4 FlPink   = new(0xe2/255f, 0x68/255f, 0xa8/255f, 1f);
    public static readonly Vector4 FlGold   = new(0xe5/255f, 0xcc/255f, 0x80/255f, 1f);

    // 百分位值（0–100）→ FFLogs 段色。PR 與 rDPS% 皆用此上色。
    public static Vector4 ParseColor(double pct) => pct switch
    {
        >= 100 => FlGold,
        >= 99  => FlPink,
        >= 95  => FlOrange,
        >= 75  => FlPurple,
        >= 50  => FlBlue,
        >= 25  => FlGreen,
        _      => FlGrey,
    };

    // PR 上色：直接套 FFLogs 段色（小樣本的提醒由 tooltip 處理，不改色以保持一致）。
    public static Vector4 PrColor(int pr, bool lowSample) => ParseColor(pr);

    // 職業縮寫 → 色；查無回淡灰。
    public static Vector4 JobColorOf(string abbrev) =>
        JobColors.TryGetValue(abbrev, out var c) ? c : Dim;

    public static string ShortenBossName(string name) =>
        name.EndsWith(" Savage", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

    // FFLogs 報告代碼 → 報告網址（只存 code，URL 於顯示端組成，見 RankingEntry.ReportCode）。
    public static string ReportUrl(string reportCode) =>
        $"https://www.fflogs.com/reports/{reportCode}";
}
