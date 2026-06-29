using System.Numerics;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace TCRankingViewer;

/// <summary>
/// 顯示招募隊伍排名的側邊視窗，自動貼附 LookingForGroupDetail 右側。
/// </summary>
public sealed unsafe class PartyFinderWindow : Window, IDisposable
{
    private static readonly Vector4 Gold   = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 Silver = new(0.80f, 0.80f, 0.85f, 1f);
    private static readonly Vector4 Bronze = new(0.80f, 0.55f, 0.30f, 1f);
    private static readonly Vector4 Teal   = new(0.40f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 Dim    = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 White  = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 Green  = new(0.45f, 0.90f, 0.45f, 1f);
    private static readonly Vector4 Red    = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Purple = new(0.78f, 0.58f, 0.96f, 1f);   // 滅暗雲分類色

    // 24 人精簡格狀的儲存格淡色底（半透明，疊在 RowBg 之上）
    // 滅暗雲有「初通關」額外獎勵：未通關者才是首通招募的目標 → 未通關綠、已通關紅
    private static readonly Vector4 GridUncleared = new(0.25f, 0.60f, 0.32f, 0.45f); // 淡綠：未通關（首通目標）
    private static readonly Vector4 GridCleared   = new(0.66f, 0.26f, 0.26f, 0.42f); // 淡紅：已通關（無首通獎勵）
    private static readonly Vector4 GridNeutral   = new(0.30f, 0.30f, 0.32f, 0.30f); // 淡灰：非滅暗雲/未知
    private static readonly Vector4 GridBlack     = new(0.40f, 0.16f, 0.16f, 0.55f); // 深紅：黑名單

    private static readonly Dictionary<string, Vector4> JobColors =
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

    private readonly HashSet<string> _expandedPlayers = [];
    private string  _categoryFilter = "全部";
    private string? _pendingCategory;          // triggers ImGuiTabItemFlags.SetSelected on next draw
    private bool    _forceDetailView;          // 24 人本時，使用者可手動切回詳細表格

    // 由 PartyFinderInspector 在招募面板開啟時呼叫，自動切換到對應副本類別
    public void SetInitialCategoryFilter(string category)
    {
        if (!string.IsNullOrEmpty(category))
        {
            _categoryFilter  = category;
            _pendingCategory = category;   // force ImGui to select the tab
        }
    }

    public PartyFinderWindow() : base(
        "招募排名##PFRankWin",
        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse)
    {
        IsOpen = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 120),
            MaximumSize = new Vector2(700, 900),
        };
        Size          = new Vector2(380, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (addonPtr == nint.Zero)
        {
            if (IsOpen) IsOpen = false;
            return;
        }

        var addon  = (AtkUnitBase*)addonPtr;
        var addonX = (float)addon->X;
        var addonY = (float)addon->Y;
        var addonW = (float)addon->GetScaledWidth(true);
        var addonH = (float)addon->GetScaledHeight(true);

        ImGui.SetNextWindowPos(new Vector2(addonX + addonW + 4, addonY), ImGuiCond.Always);

        var currentSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowSize(
            new Vector2(currentSize.X > 0 ? currentSize.X : 380f, addonH),
            ImGuiCond.Always);
    }

    public override void Draw()
    {
        // 24 人本（聯盟團，TotalSlots==24）在「滅暗雲」分頁套用精簡格狀；
        // 分類 tab 永遠顯示，使用者仍可切到其他分頁查看零式／絕等排名
        var is24 = Plugin.PartyFinderInspector.TotalSlots == 24;

        DrawHeader(is24 && _categoryFilter == "滅");
        ImGui.Separator();
        DrawCategoryTabs();

        // 僅在 24 人本 + 停在滅暗雲分頁 + 未切換詳細時，才用精簡格狀
        var useCompact = is24 && _categoryFilter == "滅" && !_forceDetailView;

        if (!Plugin.RankingService.IsReady)
        {
            ImGui.TextColored(Plugin.RankingService.IsLoading ? Dim : Red,
                Plugin.RankingService.Status);
            return;
        }

        var pending = Plugin.PartyFinderInspector.PendingLookups;
        if (pending > 0)
        {
            var filled = Plugin.PartyFinderInspector.SlotsFilled;
            var total  = Plugin.PartyFinderInspector.TotalSlots;
            if (filled > 0 && total > 0)
            {
                ImGui.TextColored(White, $"隊伍 {filled}/{total} 人，解析中 {pending} 人...");
                ImGui.Separator();
            }
            DrawLoadingTable(filled > 0 ? filled : (total > 0 ? total : 8));
            return;
        }

        var results = Plugin.PartyFinderInspector.Results;
        if (results.Count == 0)
        {
            var recruiter = Plugin.PartyFinderInspector.CurrentRecruiterName;
            if (!string.IsNullOrEmpty(recruiter))
                ImGui.TextColored(Dim, $"{recruiter} 未在排行榜上");
            else
                ImGui.TextColored(Dim, "請開啟招募面板查看排名");
            return;
        }

        if (useCompact)
        {
            DrawCompactGrid(results);
            return;
        }

        var filledD           = Plugin.PartyFinderInspector.SlotsFilled;
        var totalD            = Plugin.PartyFinderInspector.TotalSlots;
        var extraUnresolvable = Math.Max(0, filledD - results.Count);
        var totalUnresolvable = results.Count(r => r.IsUnresolvable) + extraUnresolvable;
        if (filledD > 0 && totalD > 0)
        {
            var label = $"隊伍 {filledD}/{totalD} 人";
            if (totalUnresolvable > 0) label += $"（{totalUnresolvable} 人無法解析）";
            ImGui.TextColored(Dim, label);
            ImGui.Separator();
        }

        DrawTable(results, extraUnresolvable);
    }

    private void DrawCategoryTabs()
    {
        // Consume _pendingCategory this frame so SetSelected fires exactly once.
        var pending = _pendingCategory;
        _pendingCategory = null;

        if (!ImGui.BeginTabBar("##pfCatFilter")) return;
        if (TabItem("全部",    pending == "全部"))  { _categoryFilter = "全部";  ImGui.EndTabItem(); }
        if (TabItem("零式",    pending == "零式"))  { _categoryFilter = "零式";  ImGui.EndTabItem(); }
        if (TabItem("絕境戰",  pending == "絕"))    { _categoryFilter = "絕";    ImGui.EndTabItem(); }
        if (TabItem("滅暗雲",  pending == "滅"))    { _categoryFilter = "滅";    ImGui.EndTabItem(); }
        if (TabItem("極 / 幻", pending == "極/幻")) { _categoryFilter = "極/幻"; ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ImGui.NET in this Dalamud SDK has no (string, flags) overload — call native directly
    // so we can pass null for p_open (no close button) while still supplying flags.
    private static bool TabItem(string label, bool forceSelect)
    {
        var flags = forceSelect ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(label);
        byte* pLabel = stackalloc byte[byteCount + 1];
        System.Text.Encoding.UTF8.GetBytes(label, new Span<byte>(pLabel, byteCount));
        pLabel[byteCount] = 0;
        return ImGuiNative.igBeginTabItem(pLabel, null, flags) != 0;
    }

    private List<RankingEntry> FilterEntries(List<RankingEntry> entries) => _categoryFilter switch
    {
        "零式"  => entries.Where(e => e.Category == "零式").ToList(),
        "絕"    => entries.Where(e => e.Category == "絕").ToList(),
        "滅"    => entries.Where(e => e.Category == "滅").ToList(),
        "極/幻" => entries.Where(e => e.Category is "極" or "幻").ToList(),
        _       => entries,
    };

    private void DrawHeader(bool showCompactToggle)
    {
        var recruiter = Plugin.PartyFinderInspector.CurrentRecruiterName;
        if (!string.IsNullOrEmpty(recruiter))
        {
            ImGui.TextColored(Gold, recruiter);
            ImGui.SameLine();
            ImGui.TextColored(Dim, "的招募");
        }
        else
        {
            ImGui.TextColored(Dim, "讀取中...");
        }

        // 滅暗雲分頁（24 人本）提供精簡／詳細切換（精簡格狀 ↔ 完整表格）
        if (showCompactToggle)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(_forceDetailView ? "精簡" : "詳細"))
                _forceDetailView = !_forceDetailView;
        }

        // 右側按鈕從右往左排列:設定 → 刷新（先佔據右端再 SameLine 接上去)
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100);
        if (ImGui.SmallButton("刷新"))
        {
            var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            Plugin.PartyFinderInspector.RefreshFromAgent(addonPtr);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("設定"))
            Plugin.ConfigWindow.Toggle();
    }

    private static void DrawLoadingTable(int rowCount)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##pfranks", 7, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("玩家",  ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("現職",  ImGuiTableColumnFlags.WidthFixed,   48f);
        ImGui.TableSetupColumn("職業",  ImGuiTableColumnFlags.WidthFixed,   48f);
        ImGui.TableSetupColumn("副本",  ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("排名",  ImGuiTableColumnFlags.WidthFixed,   55f);
        ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthFixed,   68f);
        ImGui.TableSetupColumn("aDPS", ImGuiTableColumnFlags.WidthFixed,   68f);
        ImGui.TableHeadersRow();

        var btnSize = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
        for (var i = 0; i < rowCount; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Dummy(btnSize); ImGui.SameLine();
            ImGui.TextColored(Dim, "查詢中...");
            for (var col = 1; col <= 6; col++)
            { ImGui.TableSetColumnIndex(col); ImGui.TextColored(Dim, "─"); }
        }

        ImGui.EndTable();
    }

    private void DrawTable(IReadOnlyList<PartyMemberResult> results, int extraUnresolvable)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##pfranks", 7, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("玩家",  ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("現職",  ImGuiTableColumnFlags.WidthFixed,   48f);
        ImGui.TableSetupColumn("職業",  ImGuiTableColumnFlags.WidthFixed,   48f);
        ImGui.TableSetupColumn("副本",  ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("排名",  ImGuiTableColumnFlags.WidthFixed,   55f);
        ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthFixed,   68f);
        ImGui.TableSetupColumn("aDPS", ImGuiTableColumnFlags.WidthFixed,   68f);
        ImGui.TableHeadersRow();

        var btnSize      = new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());
        var bossFragment = EncounterMeta.GetBossFragmentForDutyName(
            Plugin.PartyFinderInspector.CurrentDutyName);

        foreach (var member in results)
        {
            if (member.IsUnresolvable)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Dummy(btnSize); ImGui.SameLine();
                ImGui.TextColored(Dim, "（無法解析）");
                for (var col = 1; col <= 6; col++)
                { ImGui.TableSetColumnIndex(col); ImGui.TextColored(Dim, "─"); }
                continue;
            }

            // 延遲補查：若初次解析時 RankingService 尚未就緒（_index 還沒建好），
            // Query 會回傳空 list 導致顯示「無紀錄」。每次 Draw 時若發現 entries
            // 為空就再查一次（純 dictionary lookup，O(1)），撈到資料就補上。
            if (member.Entries.Count == 0 && !string.IsNullOrEmpty(member.CharacterName))
            {
                var fresh = Plugin.RankingService.Query(member.CharacterName);
                if (fresh.Count > 0)
                {
                    member.Entries = fresh;
                    member.IsFound = true;
                }
            }

            var filtered     = FilterEntries(member.Entries);
            var sorted       = filtered.Count > 0
                ? EncounterMeta.SortByJobThenContent(filtered)
                : null;
            bool hasMultiple = sorted?.Count > 1;
            bool expanded    = hasMultiple && _expandedPlayers.Contains(member.CharacterName);

            // 摺疊時優先顯示當前副本的條目；展開時回到預設優先順序（sorted[0]）
            RankingEntry? best;
            if (!expanded && bossFragment != null && sorted?.Count > 0)
                best = sorted.FirstOrDefault(e =>
                         e.Boss.Contains(bossFragment, StringComparison.OrdinalIgnoreCase))
                       ?? sorted[0];
            else
                best = sorted?.Count > 0 ? sorted[0] : null;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            if (hasMultiple)
            {
                if (ImGui.ArrowButton($"##pfx_{member.CharacterName}",
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

            var isBlacklisted = Plugin.BlacklistService.IsBlacklisted(member.CharacterName);
            ImGui.TextColored(isBlacklisted ? Red : White, member.CharacterName);

            // 從全部 entries 選最高優先 badge，不受篩選影響
            var badge = EncounterMeta.GetBadge(member.Entries);
            if (badge != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, $"{badge}✓");
            }

            if (isBlacklisted)
            {
                ImGui.SameLine();
                DrawBlacklistButton(member.CharacterName);
            }

            // 現職欄
            ImGui.TableSetColumnIndex(1);
            if (!string.IsNullOrEmpty(member.CurrentJob))
            {
                var jc = JobColors.TryGetValue(member.CurrentJob, out var jcol) ? jcol : Dim;
                ImGui.TextColored(jc, member.CurrentJob);
            }
            else
            {
                ImGui.TextColored(Dim, "─");
            }

            if (best == null)
            {
                ImGui.TableSetColumnIndex(2); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(3); ImGui.TextColored(Dim, "無紀錄");
                ImGui.TableSetColumnIndex(4); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(5); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(6); ImGui.TextColored(Dim, "─");
                continue;
            }

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
                        ImGui.TableSetColumnIndex(1);
                        DrawEntryColumns(sorted[i]);
                    }
                }
            }
        }

        for (var i = 0; i < extraUnresolvable; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Dummy(btnSize); ImGui.SameLine();
            ImGui.TextColored(Dim, "（無法解析）");
            for (var col = 1; col <= 6; col++)
            { ImGui.TableSetColumnIndex(col); ImGui.TextColored(Dim, "─"); }
        }

        ImGui.EndTable();
    }

    // ── 24 人本精簡格狀（3 欄 × 8 列）────────────────────────────────────────────
    // 以淡色底直接呈現每位成員的滅暗雲清板狀態；綠底＝已清且當前副本為滅暗雲。
    private void DrawCompactGrid(IReadOnlyList<PartyMemberResult> results)
    {
        var filled      = Plugin.PartyFinderInspector.SlotsFilled;
        // 綠底僅在「當前副本確實是滅暗雲」時啟用（滅暗雲有初通關額外獎勵，
        // 招募者藉此一眼看出誰尚未通關）。非滅暗雲的 24 人本僅顯示成員，不上綠底。
        var chaoticDuty = EncounterMeta.IsChaoticDutyName(
            Plugin.PartyFinderInspector.CurrentDutyName);

        // 標題列：人數 + 圖例（綠＝未通關／首通目標，紅＝已通關）
        ImGui.TextColored(Dim, $"隊伍 {filled}/24 人");
        ImGui.SameLine();
        if (chaoticDuty)
        {
            ImGui.TextColored(Green, "■"); ImGui.SameLine(0, 2); ImGui.TextColored(Dim, "未通關");
            ImGui.SameLine(0, 10);
            ImGui.TextColored(Red, "■");   ImGui.SameLine(0, 2); ImGui.TextColored(Dim, "已通關");
        }
        else
        {
            ImGui.TextColored(Dim, "（非滅暗雲本）");
        }
        ImGui.Separator();

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchSame;

        // 高度 0 = 依內容自動收合，避免下方留一大塊空白；不用 ScrollY（24 格一定放得下）
        if (!ImGui.BeginTable("##pfgrid", 3, flags, new Vector2(-1, 0)))
            return;

        // 三欄概念對應聯盟 A／B／C；成員以「已解析順序」填入，
        // 因 inspector 已壓縮掉空槽，無法保證實際 A/B/C 位置，欄位僅作版面分組。
        ImGui.TableSetupColumn("##c0");
        ImGui.TableSetupColumn("##c1");
        ImGui.TableSetupColumn("##c2");

        // 每格固定兩行高度：有成員的格子是名稱＋職業兩行，空格只有一行「—」，
        // 強制最小列高讓填滿與空格的高度一致，格狀才整齊。
        var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 2f
                        + ImGui.GetStyle().CellPadding.Y * 2f;

        for (var r = 0; r < 8; r++)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
            for (var c = 0; c < 3; c++)
            {
                ImGui.TableSetColumnIndex(c);
                var idx = c * 8 + r;   // 直欄填入：col0 = 0..7、col1 = 8..15、col2 = 16..23
                DrawCompactCell(idx < results.Count ? results[idx] : null, chaoticDuty);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawCompactCell(PartyMemberResult? member, bool chaoticDuty)
    {
        // 空槽：留白佔位，保持格狀對齊
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

        // 延遲補查（同 DrawTable）：RankingService 較晚就緒時補上 entries
        if (member.Entries.Count == 0 && !string.IsNullOrEmpty(member.CharacterName))
        {
            var fresh = Plugin.RankingService.Query(member.CharacterName);
            if (fresh.Count > 0) { member.Entries = fresh; member.IsFound = true; }
        }

        var chaoticClear = member.Entries.FirstOrDefault(e => e.Category == "滅" && !e.IsProg);
        var chaoticProg  = chaoticClear == null
            ? member.Entries.FirstOrDefault(e => e.Category == "滅" && e.IsProg)
            : null;
        var cleared       = chaoticClear != null;
        var isBlacklisted = Plugin.BlacklistService.IsBlacklisted(member.CharacterName);

        // 底色優先序：黑名單 > 已通關紅 > 未通關綠（皆限當前為滅暗雲）> 一般淡灰
        Vector4 bg;
        if (isBlacklisted)                bg = GridBlack;
        else if (chaoticDuty && cleared)  bg = GridCleared;    // 已通關 → 紅
        else if (chaoticDuty)             bg = GridUncleared;  // 未通關 → 綠（首通目標）
        else                              bg = GridNeutral;    // 非滅暗雲本
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(bg));

        // 第一行：名稱（底色已表達通關狀態，名稱僅黑名單標紅）
        ImGui.TextColored(isBlacklisted ? Red : White, member.CharacterName);
        if (isBlacklisted && ImGui.IsItemHovered())
        {
            var note = Plugin.BlacklistService.GetNote(member.CharacterName);
            ImGui.SetTooltip(string.IsNullOrEmpty(note) ? "黑名單" : $"黑名單：{note}");
        }

        // 第二行：現職 + 滅暗雲狀態
        //   已通關 → 滅暗雲排名 #N；進度中 → 相位；未通關 → 顯示其他副本資歷 badge，無則「未通關」
        if (!string.IsNullOrEmpty(member.CurrentJob))
        {
            var jc = JobColors.TryGetValue(member.CurrentJob, out var jcol) ? jcol : Dim;
            ImGui.TextColored(jc, member.CurrentJob);
            ImGui.SameLine(0, 4);
        }
        if (cleared)
        {
            ImGui.TextColored(RankColor(chaoticClear!.Rank), $"#{chaoticClear.Rank}");
        }
        else if (chaoticProg != null)
        {
            ImGui.TextColored(Teal, chaoticProg.PhaseNumber > 0 ? $"P{chaoticProg.PhaseNumber}" : "進度");
        }
        else
        {
            // 未通關滅暗雲：改顯示該玩家最高的其他副本清板資歷，讓招募者評估實力
            var badge = EncounterMeta.GetBadge(member.Entries);
            if (badge != null) ImGui.TextColored(Dim, $"{badge}✓");
            else               ImGui.TextColored(Dim, "未通關");
        }
    }

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
            ImGui.TextColored(color, $"  ▸{label}");
            for (var c = 1; c <= 6; c++)
            { ImGui.TableSetColumnIndex(c); }

            foreach (var e in sorted)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TableSetColumnIndex(1);
                DrawEntryColumns(e);
            }
        }
    }

    private static void DrawEntryColumns(RankingEntry e)
    {
        var abbrev    = string.IsNullOrEmpty(e.Job) ? "─" : JobAbbrev.Get(e.Job);
        var jColor    = (e.IsObsolete || e.IsProg) ? Dim : (JobColors.TryGetValue(abbrev, out var jc) ? jc : Dim);
        var textColor = (e.IsObsolete || e.IsProg) ? Dim : White;

        ImGui.TableSetColumnIndex(2); ImGui.TextColored(jColor, abbrev);

        if (e.IsProg)
        {
            ImGui.TableSetColumnIndex(3);
            ImGui.TextColored(Dim, ShortenBossName(e.Boss));
            ImGui.SameLine();
            ImGui.TextColored(Teal, "未通關");
            var progLabel = e.PhaseNumber > 0 ? $"P{e.PhaseNumber}" : "進度中";
            ImGui.TableSetColumnIndex(4); ImGui.TextColored(Teal, progLabel);
            ImGui.TableSetColumnIndex(5); ImGui.TextColored(Dim, "─");
            ImGui.TableSetColumnIndex(6); ImGui.TextColored(Dim, "─");
        }
        else
        {
            ImGui.TableSetColumnIndex(3); ImGui.TextColored(Dim, ShortenBossName(e.Boss));
            ImGui.TableSetColumnIndex(4); ImGui.TextColored(e.IsObsolete ? Dim : RankColor(e.Rank), $"#{e.Rank}");
            ImGui.TableSetColumnIndex(5); ImGui.TextColored(textColor, $"{e.Rdps:F0}");
            ImGui.TableSetColumnIndex(6); ImGui.TextColored(Dim, $"{e.Adps:F0}");
        }
    }

    // 黑名單 ✕ 按鈕：點擊後顯示備註 popup
    private static void DrawBlacklistButton(string characterName)
    {
        var popupId = $"blnote_{characterName}";
        ImGui.PushStyleColor(ImGuiCol.Button,        Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.40f, 0.40f, 0.3f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.95f, 0.40f, 0.40f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text,          Red);
        if (ImGui.SmallButton($"黑名單Ｘ##{popupId}"))
            ImGui.OpenPopup(popupId);
        ImGui.PopStyleColor(4);

        if (ImGui.BeginPopup(popupId))
        {
            ImGui.TextColored(Red, characterName);
            ImGui.Separator();
            var note = Plugin.BlacklistService.GetNote(characterName);
            if (!string.IsNullOrEmpty(note))
                ImGui.TextUnformatted(note);
            else
                ImGui.TextColored(Dim, "（無備註）");
            ImGui.EndPopup();
        }
    }

    private static Vector4 RankColor(int rank) => rank switch
    {
        <= 3  => Gold,
        <= 10 => White,
        <= 50 => Bronze,
        _     => Dim,
    };

    private static string ShortenBossName(string name) =>
        name.EndsWith(" Savage", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

    public void Dispose() { }
}
