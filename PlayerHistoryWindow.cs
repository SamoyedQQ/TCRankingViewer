using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace TCRankingViewer;

/// <summary>
/// 單一玩家的 TC 排名履歷視窗。由右鍵選單或主視窗名稱點擊開啟（見 PlayerContextMenu）。
/// 資料全部來自已下載並索引於記憶體的排名資料，開啟時即時查詢，不觸發任何網路請求或角色卡。
/// </summary>
public class PlayerHistoryWindow : Window, IDisposable
{
    private static readonly Vector4 Teal  = RankColors.Teal;
    private static readonly Vector4 Dim   = RankColors.Dim;
    private static readonly Vector4 White = RankColors.White;

    private string _name  = "";
    private string _world = "";
    private List<RankingEntry> _entries = [];

    public PlayerHistoryWindow() : base("TC 履歷###TCRankHistWin", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 160),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(560, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>開啟（或以新玩家取代）履歷視窗。world 為 null 時回退為 best-guess 查詢。</summary>
    public void Open(string name, string? world)
    {
        _name    = name;
        _world   = world ?? "";
        _entries = Plugin.RankingService.Query(name, string.IsNullOrEmpty(_world) ? null : _world);
        // 用 ### 讓視窗 ID 固定為 TCRankHistWin，不隨顯示標題（玩家名）改變，
        // 這樣 ImGui 才會記住使用者調整過的視窗尺寸（換玩家開啟不重置）
        WindowName = string.IsNullOrEmpty(_world)
            ? $"履歷 — {name}###TCRankHistWin"
            : $"履歷 — {name} @ {_world}###TCRankHistWin";
        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        // 整體字體＋icon 縮放：以實際字級重建的清晰字型（見 ScaledFont），全程 using 包住繪製。
        using var _font = Plugin.ScaledFont.Push();

        if (!Plugin.RankingService.IsReady)
        {
            ImGui.TextColored(Dim, "排名資料尚未載入，請稍候或至設定頁確認許可證金鑰。");
            return;
        }

        if (_entries.Count == 0)
        {
            ImGui.TextColored(Dim, $"{_name} 未在 TC 排名資料庫中找到紀錄（未上榜）。");
            return;
        }

        // ── 標頭：徽章 + 摘要 ──────────────────────────────────────────────────
        var badge = EncounterMeta.GetBadge(_entries);
        if (badge != null)
        {
            ImGui.TextColored(RankColors.Green, $"{badge}✓");
            ImGui.SameLine();
        }
        var prs = _entries.Where(e => e is { IsProg: false } && e.Pr.HasValue)
                          .Select(e => e.Pr!.Value).ToList();
        var summary = prs.Count > 0
            ? $"最佳 PR {prs.Max()} · 平均 PR {(int)prs.Average()} · {_entries.Count} 筆紀錄"
            : $"{_entries.Count} 筆紀錄";
        ImGui.TextColored(Dim, summary);
        ImGui.Separator();

        var hasReports = _entries.Any(e => !string.IsNullOrEmpty(e.ReportCode));

        // 各類別先分好、排序好並過濾空類別，之後每個非空類別給一個分頁。
        // 相較舊版全部垂直堆疊，分頁讓玩家一次只看單一類別，畫面清爽。
        var groups = GroupByCategory()
            .Select(g => (g.label, rows: EncounterMeta.SortByJobThenContent(g.rows)))
            .Where(g => g.rows.Count > 0)
            .ToList();

        if (groups.Count == 0) return;

        // 分頁列容許左右捲動，類別多時不會擠壓變形
        if (!ImGui.BeginTabBar("##hist_categories", ImGuiTabBarFlags.FittingPolicyScroll))
            return;

        foreach (var (label, rows) in groups)
        {
            if (!ImGui.BeginTabItem($"{label}###hist_tab_{label}"))
                continue;

            ImGui.Spacing();
            DrawCategory(rows, hasReports);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // 依類別分段（分頁順序）：絕 → 零式 → 極/幻 → 滅 → 過版本
    private IEnumerable<(string label, IEnumerable<RankingEntry> rows)> GroupByCategory()
    {
        yield return ("絕境戰", _entries.Where(e => e.Category == "絕"  && !e.IsObsolete));
        yield return ("零式",   _entries.Where(e => e.Category == "零式" && !e.IsObsolete));
        yield return ("極 / 幻", _entries.Where(e => e.Category is "極" or "幻" && !e.IsObsolete));
        yield return ("滅暗雲", _entries.Where(e => e.Category == "滅"  && !e.IsObsolete));
        yield return ("過版本", _entries.Where(e => e.IsObsolete));
    }

    // 同一分頁內的表格共用旗標：只留列間水平分隔線；
    // SizingFixedFit 讓各欄依內容自動貼合，副本欄依內容伸縮不留長空白
    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
        ImGuiTableFlags.NoHostExtendX | ImGuiTableFlags.SizingFixedFit;

    // 通關與練習資料本質不同（名次/PR/rDPS vs 相位/boss 剩HP/完成度），
    // 拆成兩張各自表頭正確的表，避免共用表頭時「表頭對不上資料、兩個百分比易混淆」。
    private void DrawCategory(List<RankingEntry> rows, bool hasReports)
    {
        var cleared = rows.Where(e => !e.IsProg).ToList();
        var prog    = rows.Where(e => e.IsProg).ToList();

        // 同時有兩類時才加小標題區分；只有一類時省略，避免多數全通關分頁多出贅字
        var showLabels = cleared.Count > 0 && prog.Count > 0;

        if (cleared.Count > 0)
        {
            if (showLabels) SectionLabel("已通關", White);
            DrawClearedTable(cleared, hasReports);
        }

        if (prog.Count > 0)
        {
            if (showLabels) SectionLabel("練習中", Teal);
            DrawProgTable(prog);
        }
    }

    // 區塊小標題（同分頁同時有通關＋練習時才顯示）
    private static void SectionLabel(string text, Vector4 color)
    {
        ImGui.Spacing();
        ImGui.TextColored(color, text);
    }

    // 已通關表：名次 / PR / rDPS% / rDPS（＋自選欄＋報告）
    private void DrawClearedTable(List<RankingEntry> rows, bool hasReports)
    {
        var extra = Plugin.Configuration.GetExtraColumns();
        var cols  = 6 + extra.Count + (hasReports ? 1 : 0);
        if (!ImGui.BeginTable($"##histclr_{rows[0].Category}_{rows[0].Boss}", cols, TableFlags, new Vector2(-1, 0)))
            return;

        ImGui.TableSetupColumn("##job", ImGuiTableColumnFlags.WidthFixed,    RankCells.JobIconColumnWidth);
        ImGui.TableSetupColumn("副本",   ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("名次",   ImGuiTableColumnFlags.WidthFixed,   52f);
        ImGui.TableSetupColumn("PR",     ImGuiTableColumnFlags.WidthFixed,   40f);
        ImGui.TableSetupColumn("rDPS%",  ImGuiTableColumnFlags.WidthFixed,   58f);
        ImGui.TableSetupColumn("rDPS",   ImGuiTableColumnFlags.WidthFixed,   70f);
        foreach (var key in extra)
            ImGui.TableSetupColumn(RankCells.OptionalHeader(key),
                ImGuiTableColumnFlags.WidthFixed, RankCells.OptionalWidth(key));
        if (hasReports)
            ImGui.TableSetupColumn("報告", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableHeadersRow();

        // rows 已依職業排序，同職業連續：只在職業變動時顯示 icon，避免整排重複同一圖示
        byte prevJob = 0;
        foreach (var e in rows)
        {
            ImGui.TableNextRow();
            var jid = JobAbbrev.GetJobId(e.Job);
            ImGui.TableSetColumnIndex(0);
            RankCells.DrawJobIcon(e.Job, dim: e.IsObsolete, show: jid != prevJob);
            prevJob = jid;
            ImGui.TableSetColumnIndex(1);
            RankCells.CellText(Dim, RankColors.ShortenBossName(e.Boss));
            RankCells.DrawSecondaryTooltip(e);

            ImGui.TableSetColumnIndex(2);
            RankCells.CellText(e.IsObsolete ? Dim : RankColors.RankColor(e.Rank), $"#{e.Rank}");
            ImGui.TableSetColumnIndex(3); RankCells.DrawPr(e);
            ImGui.TableSetColumnIndex(4); RankCells.DrawRdpsPct(e);
            ImGui.TableSetColumnIndex(5); RankCells.CellText(e.IsObsolete ? Dim : White, $"{e.Rdps:F0}");

            var ci = 6;
            foreach (var key in extra)
            { ImGui.TableSetColumnIndex(ci++); RankCells.DrawOptionalCell(e, key); }
            if (hasReports)
            { ImGui.TableSetColumnIndex(ci); RankCells.DrawReportLink(e); }
        }

        ImGui.EndTable();
    }

    // 練習中表：專屬表頭，最遠相位 / BOSS 剩HP / 完成度，各數字各有欄名不再混淆。
    // 進度資料無 report_code 與 aDPS/GCD 等次要指標，故不含報告與自選欄，保持精簡。
    private void DrawProgTable(List<RankingEntry> rows)
    {
        if (!ImGui.BeginTable($"##histprog_{rows[0].Category}", 5, TableFlags, new Vector2(-1, 0)))
            return;

        ImGui.TableSetupColumn("##job",    ImGuiTableColumnFlags.WidthFixed, RankCells.JobIconColumnWidth);
        ImGui.TableSetupColumn("副本",     ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("最遠相位", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("BOSS剩HP", ImGuiTableColumnFlags.WidthFixed, 78f);
        ImGui.TableSetupColumn("完成度",   ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableHeadersRow();

        // 練習中職業以半透明 icon 呈現（尚未定裝、非正式戰績）；同職業連續列只顯示第一個
        byte prevJob = 0;
        foreach (var e in rows)
        {
            ImGui.TableNextRow();
            var jid = JobAbbrev.GetJobId(e.Job);
            ImGui.TableSetColumnIndex(0);
            RankCells.DrawJobIcon(e.Job, dim: true, show: jid != prevJob);
            prevJob = jid;
            ImGui.TableSetColumnIndex(1); RankCells.CellText(Dim, RankColors.ShortenBossName(e.Boss));

            var phase = !string.IsNullOrEmpty(e.FurthestPhase) ? e.FurthestPhase : "進度中";
            ImGui.TableSetColumnIndex(2); RankCells.CellText(Teal, phase);
            ImGui.TableSetColumnIndex(3); RankCells.CellText(Teal, $"{e.BossPct:F1}%%");
            ImGui.TableSetColumnIndex(4); RankCells.CellText(Teal, $"{100 - e.FightPct:F1}%%");
        }

        ImGui.EndTable();
    }

    public void Dispose() { }
}
