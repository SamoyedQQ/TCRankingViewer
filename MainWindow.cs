using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace TCRankingViewer;

public class MainWindow : Window, IDisposable
{
    // 共用配色統一取自 RankColors，確保與履歷視窗一致（見 RankColors 註解）
    private static readonly Vector4 Gold    = RankColors.Gold;
    private static readonly Vector4 Silver  = RankColors.Silver;
    private static readonly Vector4 Bronze  = RankColors.Bronze;
    private static readonly Vector4 Green   = RankColors.Green;
    private static readonly Vector4 Red     = RankColors.Red;
    private static readonly Vector4 Dim     = RankColors.Dim;
    private static readonly Vector4 White   = RankColors.White;
    private static readonly Vector4 Teal    = RankColors.Teal;
    private static readonly Vector4 Purple  = RankColors.Purple;   // 滅暗雲分類色

    // 滅暗雲 3×8 精簡格狀的儲存格淡色底（與 PartyFinderWindow 同步維護）
    // 滅暗雲有「初通關」額外獎勵：未通關者才是首通招募目標 → 未通關綠、已通關紅
    private static readonly Vector4 GridUncleared = new(0.25f, 0.60f, 0.32f, 0.45f); // 淡綠：未通關
    private static readonly Vector4 GridCleared   = new(0.66f, 0.26f, 0.26f, 0.42f); // 淡紅：已通關
    private static readonly Vector4 GridNeutral   = new(0.30f, 0.30f, 0.32f, 0.30f); // 淡灰：未知
    private static readonly Vector4 GridBlack     = new(0.40f, 0.16f, 0.16f, 0.55f); // 深紅：黑名單

    private static readonly Dictionary<string, Vector4> JobColors = RankColors.JobColors;

    private readonly HashSet<string> _expandedPlayers = new();

    // 職業 icon 去重用：同職業連續列只在第一列顯示 icon。以「單一成員（及其分類區塊）」為範圍，
    // 每位成員／每個分類群組開始前歸零，避免跨成員誤藏職業。
    private byte _prevJobIcon;
    private string _categoryFilter = "全部";
    private List<string> _extra = [];   // 本次繪製套用的可選欄（每幀由設定讀取）

    public MainWindow() : base(
        "TC Savage 排名##TCRankMainWin",
        ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 150),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(760, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // 整體字體＋icon 縮放：以實際字級重建的清晰字型（見 ScaledFont），全程 using 包住繪製。
        using var _font = Plugin.ScaledFont.Push();

        DrawToolbar();
        ImGui.Separator();
        DrawCategoryTabs();
        DrawStatus();
        ImGui.Separator();
        // 滅暗雲分頁改用 3×8 精簡格狀（綠=未通關／紅=已通關），其餘分頁維持原表格
        if (_categoryFilter == "滅")
            DrawChaoticGrid();
        else
            DrawTable();
    }

    private void DrawToolbar()
    {
        // 工具列改用圖示按鈕（懸浮顯示說明），省空間、對齊主流插件觀感
        if (IconButton("##refreshParty", FontAwesomeIcon.Sync, "重新整理隊伍"))
            Plugin.PartyWatcher.RebuildResults();

        ImGui.SameLine();

        if (Plugin.RankingService.IsLoading)
        {
            ImGui.BeginDisabled();
            ImGuiComponents.IconButton("##downloading", FontAwesomeIcon.Spinner);
            ImGui.EndDisabled();
        }
        else if (IconButton("##redownload", FontAwesomeIcon.Download, "重新下載資料（強制）"))
        {
            _ = Plugin.RankingService.RefreshAsync(force: true)
                .ContinueWith(_ => Plugin.PartyWatcher.RebuildResults());
        }

        ImGui.SameLine();
        if (IconButton("##refreshBlacklist", FontAwesomeIcon.Ban, "重整黑名單"))
            Plugin.BlacklistService.RefreshFromGame();

        ImGui.SameLine();
        if (IconButton("##openConfig", FontAwesomeIcon.Cog, "設定"))
            Plugin.ConfigWindow.IsOpen = true;
    }

    // 圖示按鈕 + 懸浮說明；回傳是否被點擊
    private static bool IconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        var clicked = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void DrawCategoryTabs()
    {
        if (!ImGui.BeginTabBar("##catFilter")) return;

        if (ImGui.BeginTabItem("全部"))    { _categoryFilter = "全部";  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("零式"))    { _categoryFilter = "零式";  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("絕境戰"))  { _categoryFilter = "絕";    ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("滅暗雲"))  { _categoryFilter = "滅";    ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("極 / 幻")) { _categoryFilter = "極/幻"; ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    private static void DrawStatus()
    {
        var color = Plugin.RankingService.IsReady ? Green : Red;
        ImGui.TextColored(color, Plugin.RankingService.Status);
    }

    // ── 滅暗雲 3×8 精簡格狀（隊伍成員通關狀態一覽）──────────────────────────────
    // 與 PartyFinderWindow.DrawCompactGrid／DrawCompactCell 為同步維護的重複實作；
    // 改動其一務必同步另一邊（見 CLAUDE.md 規範）。差異：本視窗永遠視為滅暗雲情境
    // （使用者主動點選滅暗雲分頁），故一律上綠／紅底；並標示自己（★）。
    private void DrawChaoticGrid()
    {
        var results = Plugin.PartyWatcher.CurrentResults;
        if (results.Count == 0)
        {
            ImGui.TextColored(Dim, "目前沒有隊伍成員。請進入隊伍後點「重新整理隊伍」。");
            return;
        }

        // 圖例：綠＝未通關、紅＝已通關
        ImGui.TextColored(Green, "■"); ImGui.SameLine(0, 2); ImGui.TextColored(Dim, "未通關");
        ImGui.SameLine(0, 10);
        ImGui.TextColored(Red, "■");   ImGui.SameLine(0, 2); ImGui.TextColored(Dim, "已通關");
        ImGui.Separator();

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;

        if (!ImGui.BeginTable("##chaoticgrid", 3, flags, new Vector2(-1, 0)))
            return;

        ImGui.TableSetupColumn("##c0");
        ImGui.TableSetupColumn("##c1");
        ImGui.TableSetupColumn("##c2");

        // 至少 8 列（聯盟 3×8）；成員超過 24 時自動增列，避免漏顯示
        var rows      = Math.Max(8, (results.Count + 2) / 3);
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2f
                        + ImGui.GetStyle().CellPadding.Y * 2f;

        for (var r = 0; r < rows; r++)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
            for (var c = 0; c < 3; c++)
            {
                ImGui.TableSetColumnIndex(c);
                var idx = c * rows + r;   // 直欄填入
                DrawCompactCell(idx < results.Count ? results[idx] : null);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawCompactCell(PartyMemberResult? member)
    {
        // 空格佔位
        if (member == null ||
            (string.IsNullOrEmpty(member.CharacterName) && !member.IsUnresolvable))
        {
            ImGui.TextColored(Dim, "—");
            return;
        }

        if (member.IsUnresolvable)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(GridNeutral));
            ImGui.TextColored(Dim, "（未解析）");
            return;
        }

        // 延遲補查：RankingService 較晚就緒時補上 entries
        if (member.Entries.Count == 0 && !string.IsNullOrEmpty(member.CharacterName))
        {
            var fresh = Plugin.RankingService.Query(member.CharacterName, member.WorldName);
            if (fresh.Count > 0) { member.Entries = fresh; member.IsFound = true; }
        }

        var chaoticClear = member.Entries.FirstOrDefault(e => e.Category == "滅" && !e.IsProg);
        var chaoticProg  = chaoticClear == null
            ? member.Entries.FirstOrDefault(e => e.Category == "滅" && e.IsProg)
            : null;
        var cleared       = chaoticClear != null;
        var isBlacklisted = Plugin.BlacklistService.IsMarked(member.CharacterName);

        // 底色：黑名單 > 已通關紅 > 未通關綠
        var bg = isBlacklisted ? GridBlack : (cleared ? GridCleared : GridUncleared);
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(bg));

        // 第一行：名稱（自己標 ★ 金色、黑名單紅、其餘白）
        var nameColor = isBlacklisted ? Red : (member.IsSelf ? Gold : White);
        ImGui.TextColored(nameColor, (member.IsSelf ? "★ " : "") + member.CharacterName);
        if (isBlacklisted && ImGui.IsItemHovered())
            ImGui.SetTooltip(Plugin.BlacklistService.TooltipText(member.CharacterName));

        // 第二行：滅暗雲狀態（已通關排名／進度相位／其他副本資歷 badge／未通關）
        if (cleared)
        {
            ImGui.TextColored(RankColors.RankColor(chaoticClear!.Rank), $"#{chaoticClear.Rank}");
        }
        else if (chaoticProg != null)
        {
            var cpl = !string.IsNullOrEmpty(chaoticProg.FurthestPhase) ? chaoticProg.FurthestPhase : "進度";
            ImGui.TextColored(Teal, $"{cpl}({chaoticProg.BossPct:F1}%%)");
        }
        else
        {
            var badge = EncounterMeta.GetBadge(member.Entries);
            if (badge != null) ImGui.TextColored(Dim, $"{badge}✓");
            else               ImGui.TextColored(Dim, "未通關");
        }
    }

    private void DrawTable()
    {
        var results = Plugin.PartyWatcher.CurrentResults;

        if (results.Count == 0)
        {
            ImGui.TextColored(Dim, "目前沒有隊伍成員。請進入隊伍後點「重新整理隊伍」。");
            return;
        }

        // 只留列間水平分隔線（乾淨）；SizingFixedFit：各欄依內容自動貼合，多餘寬度留在表格外，
        // 不會像 stretch 那樣把空白塞進副本欄。玩家/副本欄依內容自動伸縮，避免被擠掉或留長空白。
        const ImGuiTableFlags flags =
            ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.RowBg         |
            ImGuiTableFlags.ScrollY       |
            ImGuiTableFlags.NoHostExtendX |   // 表格寬度貼齊內容，避免右側出現空白交錯條
            ImGuiTableFlags.SizingFixedFit;

        _extra = Plugin.Configuration.GetExtraColumns();

        if (!ImGui.BeginTable("##ranks", 7 + _extra.Count, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("玩家名稱", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##job",    ImGuiTableColumnFlags.WidthFixed,   RankCells.JobIconColumnWidth);
        ImGui.TableSetupColumn("副本",     ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("名次",     ImGuiTableColumnFlags.WidthFixed,   52f);
        ImGui.TableSetupColumn("PR",       ImGuiTableColumnFlags.WidthFixed,   40f);
        // rDPS% / rDPS 依內容自動貼合（進度列顯示較長的「剩HP XX%」「完成 XX%」，且隨字級縮放）
        ImGui.TableSetupColumn("rDPS%",    ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("rDPS",     ImGuiTableColumnFlags.WidthFixed);
        foreach (var key in _extra)
            ImGui.TableSetupColumn(RankCells.OptionalHeader(key),
                ImGuiTableColumnFlags.WidthFixed, RankCells.OptionalWidth(key));
        ImGui.TableHeadersRow();

        var btnSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

        foreach (var member in results)
        {
            // 依選擇的類別篩選條目
            var filtered = FilterEntries(member.Entries);
            var sorted   = filtered.Count > 0
                ? EncounterMeta.SortByJobThenContent(filtered)
                : null;
            var best        = sorted?.Count > 0 ? sorted[0] : null;
            bool hasMultiple = sorted?.Count > 1;
            bool expanded   = hasMultiple && _expandedPlayers.Contains(member.CharacterName);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            if (hasMultiple)
            {
                if (ImGui.ArrowButton($"##x_{member.CharacterName}",
                        expanded ? ImGuiDir.Down : ImGuiDir.Right))
                {
                    if (expanded) _expandedPlayers.Remove(member.CharacterName);
                    else          _expandedPlayers.Add(member.CharacterName);
                    expanded = !expanded;
                }
            }
            else
            {
                ImGui.Dummy(btnSize);
            }
            ImGui.SameLine();

            var isBlacklisted = Plugin.BlacklistService.IsMarked(member.CharacterName);
            var nameColor = isBlacklisted ? Red : (member.IsSelf ? Gold : White);
            ImGui.AlignTextToFramePadding();   // 名字與同列箭頭/icon 垂直置中，不靠上飄
            ImGui.TextColored(nameColor, (member.IsSelf ? "★ " : "") + member.CharacterName);
            // 左鍵點名字開啟該玩家履歷視窗（與右鍵選單同一功能）
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
                Plugin.PlayerHistoryWindow.Open(member.CharacterName, member.WorldName);
            // 黑名單改為懸浮在名字上顯示「黑名單：＂原因＂」，不再另加 Ｘ 按鈕
            if (isBlacklisted && ImGui.IsItemHovered())
                ImGui.SetTooltip(Plugin.BlacklistService.TooltipText(member.CharacterName));
            if (!string.IsNullOrEmpty(member.WorldName))
            {
                ImGui.SameLine();
                ImGui.TextColored(Dim, $"@{member.WorldName}");
            }

            // 最高優先清關 badge（永遠從全部 entries 選，不受篩選影響）
            var badge = EncounterMeta.GetBadge(member.Entries);
            if (badge != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, $"{badge}✓");
            }

            if (best == null)
            {
                ImGui.TableSetColumnIndex(1); RankCells.CellText(Dim, "─");
                ImGui.TableSetColumnIndex(2);
                RankCells.CellText(Dim, member.IsFound ? "此類別無資料" : "未上榜");
                ImGui.TableSetColumnIndex(3); RankCells.CellText(Dim, "─");
                ImGui.TableSetColumnIndex(4); RankCells.CellText(Dim, "─");
                ImGui.TableSetColumnIndex(5); RankCells.CellText(Dim, "─");
                ImGui.TableSetColumnIndex(6); RankCells.CellText(Dim, "─");
                for (var i = 0; i < _extra.Count; i++)
                { ImGui.TableSetColumnIndex(7 + i); RankCells.CellText(Dim, "─"); }
                continue;
            }

            _prevJobIcon = 0;   // 每位成員的連續列重新起算 icon 去重
            DrawEntryColumns(best);

            if (expanded && sorted != null)
            {
                if (_categoryFilter == "全部")
                    DrawCategoryRows(sorted.Skip(1).ToList());
                else
                {
                    for (var i = 1; i < sorted.Count; i++)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        DrawEntryColumns(sorted[i]);
                    }
                }
            }
        }

        ImGui.EndTable();
    }

    private List<RankingEntry> FilterEntries(List<RankingEntry> entries) => _categoryFilter switch
    {
        "零式"  => entries.Where(e => e.Category == "零式").ToList(),
        "絕"    => entries.Where(e => e.Category == "絕").ToList(),
        "滅"    => entries.Where(e => e.Category == "滅").ToList(),
        "極/幻" => entries.Where(e => e.Category is "極" or "幻").ToList(),
        _       => entries,
    };

    // 展開時輸出分類區塊（僅在「全部」模式使用）
    private void DrawCategoryRows(List<RankingEntry> entries)
    {
        var groups = new (string label, Vector4 color, IEnumerable<RankingEntry> rows)[]
        {
            ("絕境戰", Gold,   entries.Where(e => e.Category == "絕"  && !e.IsObsolete)),
            ("滅暗雲", Purple, entries.Where(e => e.Category == "滅"  && !e.IsObsolete)),
            ("零式",   Teal,   entries.Where(e => e.Category == "零式" && !e.IsObsolete)),
            ("極 / 幻", Silver, entries.Where(e => e.Category is "極" or "幻" && !e.IsObsolete)),
            ("過版本", Dim,    entries.Where(e => e.IsObsolete)),
        };

        foreach (var (label, color, rows) in groups)
        {
            var sorted = EncounterMeta.SortByJobThenContent(rows);
            if (sorted.Count == 0) continue;

            ImGui.TableNextRow();
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)));
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(color, $"   {label}");
            for (var c = 1; c <= 6 + _extra.Count; c++)
            { ImGui.TableSetColumnIndex(c); ImGui.TextColored(Dim, ""); }

            _prevJobIcon = 0;   // 每個分類區塊重新起算 icon 去重
            foreach (var e in sorted)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawEntryColumns(e);
            }
        }
    }

    // 欄位：1職業 2副本 3名次 4PR 5rDPS% 6rDPS（次要指標 aDPS/GCD/… 移至副本欄的懸浮提示）
    private void DrawEntryColumns(RankingEntry e)
    {
        // 職業以 icon 呈現（風格同 FFLogsViewer）；過版本/練習中半透明淡化；同職業連續列只顯示第一個
        var jid = JobAbbrev.GetJobId(e.Job);
        ImGui.TableSetColumnIndex(1);
        RankCells.DrawJobIcon(e.Job, dim: e.IsObsolete || e.IsProg, show: jid != _prevJobIcon);
        _prevJobIcon = jid;

        if (e.IsProg)
        {
            ImGui.TableSetColumnIndex(2);
            RankCells.CellText(Dim, ShortenBossName(e.Boss));
            var phase = !string.IsNullOrEmpty(e.FurthestPhase) ? e.FurthestPhase : "進度中";
            // ImGui 文字以 printf 解析，字面 % 需寫成 %% 才會顯示
            // rDPS% 欄改顯示當前相位 boss 剩餘血量、rDPS 欄顯示整體完成度
            ImGui.TableSetColumnIndex(3); RankCells.CellText(Teal, phase);
            ImGui.TableSetColumnIndex(4); RankCells.CellText(Dim, "─");
            ImGui.TableSetColumnIndex(5); RankCells.CellText(Teal, $"剩HP {e.BossPct:F1}%%");
            ImGui.TableSetColumnIndex(6); RankCells.CellText(Teal, $"完成 {100 - e.FightPct:F1}%%");
            for (var i = 0; i < _extra.Count; i++)
            { ImGui.TableSetColumnIndex(7 + i); RankCells.DrawOptionalCell(e, _extra[i]); }
        }
        else
        {
            ImGui.TableSetColumnIndex(2);
            RankCells.CellText(Dim, ShortenBossName(e.Boss));
            RankCells.DrawSecondaryTooltip(e);   // 懸浮副本名顯示 aDPS/GCD/uptime/死亡/報告 等次要指標

            ImGui.TableSetColumnIndex(3);
            RankCells.CellText(e.IsObsolete ? Dim : RankColors.RankColor(e.Rank), $"#{e.Rank}");
            ImGui.TableSetColumnIndex(4); RankCells.DrawPr(e);
            ImGui.TableSetColumnIndex(5); RankCells.DrawRdpsPct(e);
            ImGui.TableSetColumnIndex(6); RankCells.CellText(e.IsObsolete ? Dim : White, $"{e.Rdps:F0}");
            for (var i = 0; i < _extra.Count; i++)
            { ImGui.TableSetColumnIndex(7 + i); RankCells.DrawOptionalCell(e, _extra[i]); }
        }
    }

    private static string ShortenBossName(string name) => RankColors.ShortenBossName(name);

    public void Dispose() { }
}
