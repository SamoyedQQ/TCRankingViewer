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
    public string Job           { get; set; } = "";
    public string PlayerName    { get; set; } = "";
    public double Dps           { get; set; }
    public double Rdps          { get; set; }
    public double Adps          { get; set; }
    public double FightDuration { get; set; }
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
    [JsonPropertyName("job")]                public string Job              { get; set; } = "";
    [JsonPropertyName("dps")]                public double Dps              { get; set; }
    [JsonPropertyName("rdps")]               public double Rdps             { get; set; }
    [JsonPropertyName("adps")]               public double Adps             { get; set; }
    [JsonPropertyName("clear_time_seconds")] public double ClearTimeSeconds { get; set; }
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
    public static readonly (string Fragment, string Label)[] BadgePriority =
    [
        ("歐米茄",   "歐米茄"),
        ("龍詩戰爭", "龍詩"),
        ("巴哈姆特", "巴哈"),
        ("亞歷山大", "絕亞"),
        ("究極神兵", "神兵"),
        ("M4S",      "M4S"),
        ("M3S",      "M3S"),
        ("M2S",      "M2S"),
        ("M1S",      "M1S"),
        ("永恆女王", "極女王"),
        ("白虎",     "幻白虎"),
    ];

    public static string NormalizeBossName(string name)
        => KantaiNameOverrides.TryGetValue(name, out var n) ? n : name;

    public static bool IsObsoleteKey(string key)
        => ObsoleteKeys.Contains(key);

    // 從 ContentFinderCondition 副本名稱推導 BadgePriority fragment（用於摺疊時優先顯示當前副本）
    public static string? GetBossFragmentForDutyName(string dutyName)
    {
        if (string.IsNullOrEmpty(dutyName)) return null;
        foreach (var (fragment, _) in BadgePriority)
            if (dutyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return fragment;
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
    public List<RankingEntry> Entries { get; set; } = [];

    public RankingEntry? BestEntry =>
        Entries.Count > 0 ? Entries.MinBy(e => e.Rank) : null;
}
