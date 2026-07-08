using System.Text.Json.Serialization;

namespace TCRankingViewer;

// ─── 內部統一排名條目（由各資料來源轉換而來）──────────────────────────────────
public class RankingEntry
{
    public int    Rank          { get; set; }
    public string Boss          { get; set; } = "";
    public string Category      { get; set; } = "";
    public bool   IsObsolete    { get; set; }
    public bool   IsProg        { get; set; }   // 未通關進度條目
    public string FurthestPhase { get; set; } = "";  // 最遠相位中文名（IsProg 時使用）
    public int    PhaseNumber   { get; set; }        // 最遠相位序號（IsProg 時使用，用於顯示 P2 等）
    public double FightPct      { get; set; }   // IsProg：FFLogs 戰鬥剩餘%（完成% = 100 − 此值）
    public double BossPct       { get; set; }   // IsProg：當前最遠相位 boss 剩餘血量%
    public string Job           { get; set; } = "";
    public string PlayerName    { get; set; } = "";
    public string Server        { get; set; } = "";  // 伺服器（World）名稱，供「名稱＋伺服器」精準匹配
    public double Dps           { get; set; }
    public double Rdps          { get; set; }
    public double Adps          { get; set; }
    public double FightDuration { get; set; }

    // ── 建索引時由 RankingService 計算的顯示欄位（定義與資料站表頭一致）──────────
    // PR：同副本同職業內「贏過多少人」的百分位（PR75 = 贏過 75%）。
    // 樣本 < 20 筆時資料站不計 PR → 這裡以 null 表示，UI 顯示「—」。
    public int?  Pr          { get; set; }
    // 20~50 筆為小樣本：PR 仍計算但位階僅供參考，UI 可淡化提示。
    public bool  PrLowSample { get; set; }
    // rDPS%：本人 rDPS 達該職最高者的幾成（95 = 達頂尖 95%）。
    public double? RdpsPct   { get; set; }

    // ── 資料站原始檔提供、需後端 trimRankingFile 保留才會有值的次要指標 ───────────
    // 尚未保留前一律為 null，UI 僅在有值時顯示，避免顯示 0 誤導。
    public double? Ndps          { get; set; }  // normalized DPS
    public int?    GcdMs         { get; set; }  // 平均 GCD 配速（毫秒）；>2500 資料站一律顯示 2500
    public double? GcdUptime     { get; set; }  // GCD 銜接緊密度 %
    public double? ActivePercent { get; set; }  // 戰鬥中實際輸出時間佔比 %
    public int?    Deaths        { get; set; }  // 該場死亡數
    public string? ReportCode    { get; set; }  // FFLogs 報告代碼；URL 於顯示端組成
}

// ─── Kantai235 encounters.json ────────────────────────────────────────────────
public class KantaiEncountersRoot
{
    [JsonPropertyName("encounters")]
    public List<KantaiEncounterInfo>? Encounters { get; set; }
}

public class KantaiEncounterInfo
{
    [JsonPropertyName("key")]      public string Key      { get; set; } = "";
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    // 有此欄位（非空）代表該副本提供「最遠進度」資料；零式/絕/滅有，極/幻無
    [JsonPropertyName("progress_path")] public string? ProgressPath { get; set; }
}

// ─── Kantai235 rankings/{key}.json ────────────────────────────────────────────
public class KantaiEncounterDetail
{
    [JsonPropertyName("key")]      public string Key      { get; set; } = "";
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}

public class KantaiRankingFile
{
    [JsonPropertyName("encounter")]       public KantaiEncounterDetail?    Encounter { get; set; }
    [JsonPropertyName("ranking_entries")] public List<KantaiRankingEntry>? Entries   { get; set; }
}

public class KantaiRankingEntry
{
    [JsonPropertyName("character_name")]     public string CharacterName    { get; set; } = "";
    [JsonPropertyName("server")]             public string Server           { get; set; } = "";
    [JsonPropertyName("job")]                public string Job              { get; set; } = "";
    [JsonPropertyName("dps")]                public double Dps              { get; set; }
    [JsonPropertyName("rdps")]               public double Rdps             { get; set; }
    [JsonPropertyName("adps")]               public double Adps             { get; set; }
    [JsonPropertyName("clear_time_seconds")] public double ClearTimeSeconds { get; set; }

    // 以下欄位資料站原始檔皆有，但需後端 trimRankingFile 保留才會下傳；
    // 未保留時為 null（nullable），插件端顯示時自動略過。
    [JsonPropertyName("ndps")]               public double? Ndps           { get; set; }
    [JsonPropertyName("gcd_length_ms")]      public int?    GcdLengthMs    { get; set; }
    [JsonPropertyName("gcd_uptime")]         public double? GcdUptime      { get; set; }
    [JsonPropertyName("active_percent")]     public double? ActivePercent  { get; set; }
    [JsonPropertyName("deaths")]             public int?    Deaths         { get; set; }
    [JsonPropertyName("report_code")]        public string? ReportCode     { get; set; }
}

// ─── progress/{key}.json（最遠進度，未通關玩家）──────────────────────────────
public class KantaiProgressFile
{
    [JsonPropertyName("encounter")]        public KantaiEncounterDetail?     Encounter { get; set; }
    [JsonPropertyName("progress_entries")] public List<KantaiProgressEntry>? Entries   { get; set; }
}

public class KantaiProgressEntry
{
    [JsonPropertyName("character_name")]   public string CharacterName   { get; set; } = "";
    [JsonPropertyName("server")]           public string Server          { get; set; } = "";
    [JsonPropertyName("job")]              public string Job             { get; set; } = "";
    // FFLogs lastPhaseAsAbsoluteIndex，各副本基準不一（見 EncounterMeta.ProgPhaseLabel 對照）
    [JsonPropertyName("phase_index")]      public int    PhaseIndex      { get; set; }
    [JsonPropertyName("boss_percentage")]  public double BossPercentage  { get; set; }
    [JsonPropertyName("fight_percentage")] public double FightPercentage { get; set; }
    [JsonPropertyName("pull_count")]       public int    PullCount       { get; set; }
}

// ─── 副本元資料（名稱對照、類別分組、badge 優先級）───────────────────────────
public static class EncounterMeta
{
    // Kantai encounter key → 過版本
    private static readonly HashSet<string> ObsoleteKeys =
        new(StringComparer.OrdinalIgnoreCase)
        { "extreme_valigarmanda", "extreme_zoraal_ja" };

    // Kantai 副本名稱覆寫
    private static readonly Dictionary<string, string> KantaiNameOverrides = new()
        { { "絕 幻想龍詩", "絕 龍詩戰爭" } };

    // Badge 優先級（最難 → 最簡單），fragment 用來 Contains 比對 Boss 名稱
    // 絕伊甸（絕 伊甸 / Futures Rewritten）為目前最新且公認最難的絕本，故置於最前
    public static readonly (string Fragment, string Label)[] BadgePriority =
    [
        ("伊甸",     "伊甸"),
        ("歐米茄",   "歐米茄"),
        ("龍詩戰爭", "龍詩"),
        ("巴哈姆特", "巴哈"),
        ("亞歷山大", "絕亞"),
        ("究極神兵", "神兵"),
        ("M4S",      "M4S"),
        ("M3S",      "M3S"),
        ("M2S",      "M2S"),
        ("M1S",      "M1S"),
        ("黑暗之雲", "滅暗雲"),   // 滅 Chaotic 24 人聯盟團（The Cloud of Darkness (Chaotic)）
        ("永恆女王", "極女王"),
        ("白虎",     "幻白虎"),
    ];

    // 副本 key → 插件沿用的精簡名稱。
    // 新資料源名稱過長（如「阿卡狄亞零式登天鬥技場 輕量級4 M4S」），且部分用字與
    // BadgePriority / 摺疊比對所依賴的 fragment 不符（例：絕伊甸新名「光暗未來殲滅戰」
    // 不含「伊甸」、會導致 badge 與排序失效）。以 key 對照回舊名同時解決過長與比對問題。
    private static readonly Dictionary<string, string> CanonicalNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["savage_m1s"]                 = "零式 M1S / 黑貓",
        ["savage_m2s"]                 = "零式 M2S / 蜂蜂小甜心",
        ["savage_m3s"]                 = "零式 M3S / 野蠻炸彈",
        ["savage_m4s"]                 = "零式 M4S / 狡雷",
        ["extreme_valigarmanda"]       = "極 豔翼蛇鳥",
        ["extreme_zoraal_ja"]          = "極 佐拉加",
        ["extreme_queen_eternal"]      = "極 永恆女王",
        ["unreal_byakko"]              = "幻 白虎",
        ["chaotic_cloud_of_darkness"]  = "滅 黑暗之雲",
        ["ultimate_bahamut"]           = "絕 巴哈姆特",
        ["ultimate_ultima_weapon"]     = "絕 究極神兵",
        ["ultimate_alexander"]         = "絕 亞歷山大",
        ["ultimate_dragonsong"]        = "絕 龍詩戰爭",
        ["ultimate_omega"]             = "絕 歐米茄",
        ["ultimate_futures_rewritten"] = "絕 伊甸",
    };

    // 以 key 優先取精簡名稱；未對照的 key 退回名稱字串覆寫表
    public static string DisplayBossName(string key, string name)
        => CanonicalNames.TryGetValue(key, out var c) ? c : NormalizeBossName(name);

    public static string NormalizeBossName(string name)
        => KantaiNameOverrides.TryGetValue(name, out var n) ? n : name;

    public static bool IsObsoleteKey(string key)
        => ObsoleteKeys.Contains(key);

    // 練習進度階段對照（key = encounter key，內層 key = FFLogs lastPhaseAsAbsoluteIndex）。
    // ⚠️ 索引基準逐本不一（FFLogs 對不同本的起算與是否把中場算進 index 都不同），
    // 以上游資料站校準後的對照表為準，不可套同一公式。零式視為單階段（無此 key）。
    private static readonly Dictionary<string, Dictionary<int, string>> ProgPhases = new()
    {
        ["ultimate_futures_rewritten"] = new() { [0] = "P1", [1] = "P2", [2] = "P3", [3] = "P4", [4] = "P5" },
        ["ultimate_dragonsong"]        = new() { [0] = "P1", [1] = "P2", [2] = "P3", [3] = "P4", [4] = "間奏", [5] = "P5", [6] = "P6", [7] = "P7" },
        ["ultimate_omega"]             = new() { [0] = "P1", [1] = "P2", [2] = "P3", [3] = "P4", [4] = "P5", [5] = "P6" },
        ["ultimate_alexander"]         = new() { [0] = "P1", [1] = "間奏", [2] = "P2", [3] = "間奏", [4] = "P3", [5] = "P4" },
        ["ultimate_ultima_weapon"]     = new() { [0] = "P1", [1] = "P2", [2] = "P3", [3] = "間奏", [4] = "P4" },
        ["chaotic_cloud_of_darkness"]  = new() { [1] = "P1", [2] = "間奏", [3] = "P2", [4] = "間奏", [5] = "P3" },
    };

    // phase_index → 顯示用相位標籤。查無對照且 index>0 → 誠實顯示「第 N 階段」；
    // index ≤ 0（零式等單階段）→ 回空字串，呼叫端改顯示「進度中」。
    public static string ProgPhaseLabel(string encKey, int phaseIndex)
    {
        if (ProgPhases.TryGetValue(encKey, out var m) && m.TryGetValue(phaseIndex, out var label))
            return label;
        return phaseIndex > 0 ? $"第 {phaseIndex} 階段" : "";
    }

    // 從 ContentFinderCondition 副本名稱推導 BadgePriority fragment（用於摺疊時優先顯示當前副本）
    public static string? GetBossFragmentForDutyName(string dutyName)
    {
        if (string.IsNullOrEmpty(dutyName)) return null;
        foreach (var (fragment, _) in BadgePriority)
            if (dutyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return fragment;
        // 絕本：遊戲內官方副本名與 BadgePriority fragment 用字不一致者明確對照。
        // 例：絕伊甸遊戲內為「絕 光暗未來殲滅戰」，不含「伊甸」→ 上面迴圈無法命中，
        // 會導致招募視窗摺疊時改顯示玩家其他清板而非當前副本進度（最遠進度看不到）。
        if (dutyName.Contains("光暗未來", StringComparison.OrdinalIgnoreCase)) return "伊甸";     // 絕 光暗未來殲滅戰 (FRU)
        if (dutyName.Contains("龍詩",     StringComparison.OrdinalIgnoreCase)) return "龍詩戰爭"; // 絕 幻想龍詩戰爭 (DSR)
        // 滅 Chaotic 聯盟團：TC 副本名可能用「暗之雲」而非完整「黑暗之雲」，
        // 故 BadgePriority 迴圈的「黑暗之雲」fragment 不一定命中，這裡補寬鬆比對
        if (IsChaoticDutyName(dutyName)) return "黑暗之雲";
        // TC 別名：絕境武器討滅戰 / 究極武器 → 究極神兵
        if (dutyName.Contains("究極武器", StringComparison.OrdinalIgnoreCase) ||
            dutyName.Contains("Weapon's Refrain", StringComparison.OrdinalIgnoreCase))
            return "究極神兵";
        // TC 零式：阿卡狄亞零式登天門技場 輕量級1-4 → M1S-M4S
        if (dutyName.Contains("輕量級4")) return "M4S";
        if (dutyName.Contains("輕量級3")) return "M3S";
        if (dutyName.Contains("輕量級2")) return "M2S";
        if (dutyName.Contains("輕量級1")) return "M1S";
        return null;
    }

    // 判斷 TC 副本名是否為「滅」Chaotic 聯盟團（黑暗之雲 / The Cloud of Darkness (Chaotic)）。
    // 寬鬆比對：名稱同時含「雲」與「暗」或「闇」即視為黑暗之雲系列，容忍繁中／日文用字差異
    // （滅 黑暗之雲 / 暗之雲 / 暗闇の雲 皆命中）。用於「當前副本是否為滅暗雲」的綠底判定。
    public static bool IsChaoticDutyName(string dutyName)
    {
        if (string.IsNullOrEmpty(dutyName)) return false;
        return dutyName.Contains('雲') &&
               (dutyName.Contains('暗') || dutyName.Contains('闇'));
    }

    // 玩家是否有「滅」清板紀錄（排除未通關進度條目）
    public static bool HasChaoticClear(IEnumerable<RankingEntry> entries)
        => entries.Any(e => e.Category == "滅" && !e.IsProg);

    // 從玩家 entries 取得最高優先級 badge label（僅清板條目；找不到回傳 null）
    public static string? GetBadge(IEnumerable<RankingEntry> entries)
    {
        var bossSet = entries.Where(e => !e.IsProg).Select(e => e.Boss).ToHashSet();
        foreach (var (fragment, label) in BadgePriority)
            if (bossSet.Any(b => b.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                return label;
        return null;
    }

    // 依 BadgePriority 回傳 entry 的排序數值（數字越小 = 越高優先）
    // 進度條目加 1000 偏移，確保清板條目永遠優先顯示
    public static int GetEntryPriority(RankingEntry e)
    {
        var base_ = 999;
        for (var i = 0; i < BadgePriority.Length; i++)
            if (e.Boss.Contains(BadgePriority[i].Fragment, StringComparison.OrdinalIgnoreCase))
            { base_ = i; break; }
        return e.IsProg ? base_ + 1000 : base_;
    }

    // 依職業分組後排序：同職業放一起，各組以最高優先副本排序，組內依副本優先 → Rank
    public static List<RankingEntry> SortByJobThenContent(IEnumerable<RankingEntry> entries)
    {
        return entries
            .GroupBy(e => e.Job)
            .Select(g =>
            {
                var sorted = g.OrderBy(GetEntryPriority).ThenBy(e => e.Rank).ToList();
                return (BestPriority: GetEntryPriority(sorted[0]), BestRank: sorted[0].Rank, Entries: sorted);
            })
            .OrderBy(g => g.BestPriority)
            .ThenBy(g => g.BestRank)
            .SelectMany(g => g.Entries)
            .ToList();
    }
}

// ─── 職業名稱對照（FFLogs 英文全名 → 縮寫）─────────────────────────────────
public static class JobAbbrev
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Paladin",        "騎士" }, { "Warrior",       "戰士" },
        { "DarkKnight",     "暗騎" }, { "Dark Knight",   "暗騎" },
        { "Gunbreaker",     "絕槍" },
        { "WhiteMage",      "白魔" }, { "White Mage",    "白魔" },
        { "Scholar",        "學者" }, { "Astrologian",   "占星" },
        { "Sage",           "賢者" },
        { "Monk",           "武僧" }, { "Dragoon",       "龍騎" },
        { "Ninja",          "忍者" }, { "Samurai",       "武士" },
        { "Reaper",         "鐮魂" }, { "Viper",         "蝰蛇" },
        { "Bard",           "詩人" }, { "Machinist",     "機工" },
        { "Dancer",         "舞者" },
        { "BlackMage",      "黑魔" }, { "Black Mage",    "黑魔" },
        { "Summoner",       "召喚" }, { "RedMage",       "赤魔" },
        { "Red Mage",       "赤魔" }, { "Pictomancer",   "繪靈" },
    };

    private static readonly Dictionary<byte, string> JobIdMap = new()
    {
        { 19, "騎士" }, { 21, "戰士" }, { 32, "暗騎" }, { 37, "絕槍" },
        { 24, "白魔" }, { 28, "學者" }, { 33, "占星" }, { 40, "賢者" },
        { 20, "武僧" }, { 22, "龍騎" }, { 30, "忍者" }, { 34, "武士" },
        { 39, "鐮魂" }, { 41, "蝰蛇" },
        { 23, "詩人" }, { 31, "機工" }, { 38, "舞者" },
        { 25, "黑魔" }, { 27, "召喚" }, { 35, "赤魔" }, { 42, "繪靈" },
    };

    public static string Get(string jobFullName)
    {
        if (Map.TryGetValue(jobFullName, out var abbrev)) return abbrev;
        return jobFullName;
    }

    public static string GetByJobId(byte jobId)
    {
        return JobIdMap.TryGetValue(jobId, out var abbrev) ? abbrev : "";
    }
}

// ─── 共享黑名單條目（server sync 用）────────────────────────────────────────
// World / Cid 為新欄位：舊版插件上傳/下載都沒有，預設 null；
// Cid 用 string（uint64 在 JSON 無法安全往返），對應 FFXIV ContentId
public record BlacklistEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")]  string  Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("note")]  string  Note,
    [property: System.Text.Json.Serialization.JsonPropertyName("world")] string? World = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("cid")]   string? Cid   = null);

// ─── 隊伍成員查詢結果（聚合單一玩家所有副本）────────────────────────────────
public class PartyMemberResult
{
    public string CharacterName  { get; set; } = "";
    public string WorldName      { get; set; } = "";
    public string CurrentJob     { get; set; } = "";
    public bool   IsSelf         { get; set; }
    public bool   IsFound        { get; set; }
    public bool   IsUnresolvable { get; set; }
    // 同名橫跨多個伺服器、又拿不到此成員的 world（NameCache 解析）→ 無法確定是哪位。
    // 此時不顯示成績（以免張冠李戴），UI 改顯示驚嘆號提醒「僅供參考」。
    public bool   AmbiguousCrossServer { get; set; }
    public List<RankingEntry> Entries { get; set; } = [];

    public RankingEntry? BestEntry =>
        Entries.Count > 0 ? Entries.MinBy(e => e.Rank) : null;
}
