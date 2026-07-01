using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
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

    // ── 截圖狀態機 ──────────────────────────────────────────────────────────
    // 名稱打碼與擷取需分幀：phase 1 = 本幀套用遮罩繪製並記錄範圍；
    // phase 2 = 上一幀（遮罩）已呈現到螢幕，於本幀開頭擷取。
    private int     _shotPhase;
    private bool    _shotMask;    // 本次截圖是否打碼（點按當下由設定決定）
    private bool    _maskActive;  // 繪製名稱時是否套用遮罩，供各 Draw* 讀取
    private Vector2 _shotPos, _shotSize;

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
        // 截圖狀態機（見欄位說明）：phase 2 代表上一幀（可能已打碼）已呈現 → 於本幀開頭擷取；
        // phase 1 代表本幀為擷取前的繪製幀 → 套用遮罩並記錄視窗範圍，下一幀擷取。
        if (_shotPhase == 2)
        {
            _shotPhase = 0;   // 先清狀態，確保任何失敗都不會卡在擷取迴圈
            try { CaptureWindow(_shotPos, _shotSize); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[TCRanking] 截圖失敗"); }
        }
        _maskActive = _shotPhase == 1 && _shotMask;
        if (_shotPhase == 1)
        {
            var winPos  = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();
            // 併入左側「招募資訊」(LookingForGroupDetail addon) 範圍，讓截圖同時涵蓋兩邊視窗。
            // addon 座標與 ImGui 視窗同一 client 像素空間（PreDraw 即用它定位本視窗），故可直接取聯集。
            if (TryGetAddonRect(out var aPos, out var aSize))
            {
                var minX = Math.Min(winPos.X, aPos.X);
                var minY = Math.Min(winPos.Y, aPos.Y);
                var maxX = Math.Max(winPos.X + winSize.X, aPos.X + aSize.X);
                var maxY = Math.Max(winPos.Y + winSize.Y, aPos.Y + aSize.Y);
                // addon 回報高度略多於可見視窗（底部邊框/留白），裁掉約一行高度避免露出下方聊天。
                // 用文字行高而非寫死像素，隨字級/DPI 自動縮放。
                maxY -= ImGui.GetTextLineHeightWithSpacing();
                _shotPos  = new Vector2(minX, minY);
                _shotSize = new Vector2(maxX - minX, Math.Max(1f, maxY - minY));
            }
            else
            {
                _shotPos  = winPos;
                _shotSize = winSize;
            }
            _shotPhase = 2;
        }

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
            ImGui.TextColored(Gold, MaskName(recruiter));
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

        // 右側按鈕從左到右：截圖 → 刷新 → 設定（先跳到右端起點再 SameLine 依序接上）
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 148);
        if (ImGui.SmallButton("截圖"))
        {
            // 點按當下決定是否打碼，phase 1 讓下一幀以遮罩繪製、再下一幀擷取
            _shotMask  = Plugin.Configuration.MaskIdOnScreenshot;
            _shotPhase = 1;
        }
        ImGui.SameLine();
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
        ImGui.TableSetupColumn("現職",  ImGuiTableColumnFlags.WidthFixed,   44f);
        ImGui.TableSetupColumn("職業",  ImGuiTableColumnFlags.WidthFixed,   44f);
        ImGui.TableSetupColumn("副本",  ImGuiTableColumnFlags.WidthStretch, 2.8f);
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
        ImGui.TableSetupColumn("現職",  ImGuiTableColumnFlags.WidthFixed,   44f);
        ImGui.TableSetupColumn("職業",  ImGuiTableColumnFlags.WidthFixed,   44f);
        ImGui.TableSetupColumn("副本",  ImGuiTableColumnFlags.WidthStretch, 2.8f);
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
                var fresh = Plugin.RankingService.Query(member.CharacterName, member.WorldName);
                if (fresh.Count > 0)
                {
                    member.Entries = fresh;
                    member.IsFound = true;
                }
                else if (string.IsNullOrEmpty(member.WorldName))
                    member.AmbiguousCrossServer =
                        Plugin.RankingService.IsCrossServerAmbiguous(member.CharacterName);
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

            var isBlacklisted = Plugin.BlacklistService.IsMarked(member.CharacterName);
            ImGui.TextColored(isBlacklisted ? Red : White, MaskName(member.CharacterName));
            // 黑名單改為懸浮在名字上顯示「黑名單：＂原因＂」，不再另加 Ｘ 按鈕
            if (isBlacklisted && ImGui.IsItemHovered())
                ImGui.SetTooltip(Plugin.BlacklistService.TooltipText(member.CharacterName));
            if (!string.IsNullOrEmpty(member.WorldName))
            {
                ImGui.SameLine(0, 3);
                ImGui.TextColored(Dim, $"@{member.WorldName}");
            }
            if (member.AmbiguousCrossServer)
            {
                ImGui.SameLine(0, 3);
                ImGui.TextColored(Gold, "！");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("存在多個跨伺服器同名玩家，僅供參考");
            }

            // 從全部 entries 選最高優先 badge，不受篩選影響
            var badge = EncounterMeta.GetBadge(member.Entries);
            if (badge != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, $"{badge}✓");
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
        // 紅綠底綁定「目前正在看滅暗雲分頁」而非招募的副本名稱：
        // 練習/自訂招募常把副本欄設成非滅暗雲的 CFC（IsChaoticDutyName 會失敗），
        // 但使用者既然停在滅暗雲分頁、且精簡格只在此分頁出現，就一律套用通關紅綠底。
        var chaoticDuty = _categoryFilter == "滅";

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

    private void DrawCompactCell(PartyMemberResult? member, bool chaoticDuty)
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
            var fresh = Plugin.RankingService.Query(member.CharacterName, member.WorldName);
            if (fresh.Count > 0) { member.Entries = fresh; member.IsFound = true; }
            else if (string.IsNullOrEmpty(member.WorldName))
                member.AmbiguousCrossServer =
                    Plugin.RankingService.IsCrossServerAmbiguous(member.CharacterName);
        }

        var chaoticClear = member.Entries.FirstOrDefault(e => e.Category == "滅" && !e.IsProg);
        var chaoticProg  = chaoticClear == null
            ? member.Entries.FirstOrDefault(e => e.Category == "滅" && e.IsProg)
            : null;
        var cleared       = chaoticClear != null;
        var isBlacklisted = Plugin.BlacklistService.IsMarked(member.CharacterName);

        // 底色優先序：黑名單 > 已通關紅 > 未通關綠（皆限當前為滅暗雲）> 一般淡灰
        Vector4 bg;
        if (isBlacklisted)                bg = GridBlack;
        else if (chaoticDuty && cleared)  bg = GridCleared;    // 已通關 → 紅
        else if (chaoticDuty)             bg = GridUncleared;  // 未通關 → 綠（首通目標）
        else                              bg = GridNeutral;    // 非滅暗雲本
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(bg));

        // 第一行：名稱 + @伺服器（底色已表達通關狀態，名稱僅黑名單標紅）
        // 顯示伺服器是為了讓使用者確認匹配到的是「同名同服」的正確玩家，非跨服同名者
        ImGui.TextColored(isBlacklisted ? Red : White, MaskName(member.CharacterName));
        if (isBlacklisted && ImGui.IsItemHovered())
            ImGui.SetTooltip(Plugin.BlacklistService.TooltipText(member.CharacterName));
        if (!string.IsNullOrEmpty(member.WorldName))
        {
            ImGui.SameLine(0, 3);
            ImGui.TextColored(Dim, $"@{member.WorldName}");
        }
        if (member.AmbiguousCrossServer)
        {
            ImGui.SameLine(0, 3);
            ImGui.TextColored(Gold, "！");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("存在多個跨伺服器同名玩家，僅供參考");
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
            var cpl = !string.IsNullOrEmpty(chaoticProg.FurthestPhase) ? chaoticProg.FurthestPhase : "進度";
            ImGui.TextColored(Teal, $"{cpl}({chaoticProg.BossPct:F1}%%)");
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
            // ImGui 文字以 printf 解析，字面 % 需寫成 %% 才會顯示
            ImGui.TextColored(Teal, $"完成 {100 - e.FightPct:F1}%%");
            var phase     = !string.IsNullOrEmpty(e.FurthestPhase) ? e.FurthestPhase : "進度中";
            var progLabel = $"{phase}({e.BossPct:F1}%%)";
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

    // 截圖打碼：遮罩期間把玩家名稱換成等長的全形星號，隱藏 ID；平時原樣回傳
    private string MaskName(string name)
        => _maskActive && !string.IsNullOrEmpty(name)
            ? new string('＊', Math.Clamp(name.Length, 2, 6))
            : name;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    // 取得左側「招募資訊」視窗(addon)的 client 空間範圍，供截圖聯集使用
    private static bool TryGetAddonRect(out Vector2 pos, out Vector2 size)
    {
        pos = default; size = default;
        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (addonPtr == nint.Zero) return false;
        var addon = (AtkUnitBase*)addonPtr;
        pos  = new Vector2(addon->X, addon->Y);
        size = new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true));
        return size is { X: > 0, Y: > 0 };
    }

    // 剪貼簿 / 記憶體 Win32 API（以 CF_DIB 放入影像，免依賴 WinForms/WPF）
    private const uint CF_DIB = 8;
    private const uint GMEM_MOVEABLE = 0x0002;
    [DllImport("user32.dll")]   private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll")]   private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]   private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll")]   private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);

    // 擷取本視窗範圍並複製到剪貼簿（不存檔）。ImGui 座標為遊戲 client 區像素，換算成桌面
    // 座標後以 GDI 從螢幕複製（需視窗/邊框全螢幕；Dalamud overlay 本就要求非獨佔全螢幕）。
    private static void CaptureWindow(Vector2 pos, Vector2 size)
    {
        try
        {
            var w = (int)size.X;
            var h = (int)size.Y;
            if (w <= 0 || h <= 0) return;

            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == nint.Zero)
            {
                Plugin.ChatGui.PrintError("[TCRanking] 截圖失敗：找不到遊戲視窗");
                return;
            }

            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref origin);
            var sx = origin.X + (int)pos.X;
            var sy = origin.Y + (int)pos.Y;

            // 直接擷取為 24bpp（去掉 alpha，避免貼上時透明區被當成黑色）
            byte[] dib;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
                }
                dib = BuildDib(bmp);
            }

            // 剪貼簿寫入移至背景執行緒（OpenClipboard 偶爾被占用需重試，避免卡繪製）
            _ = Task.Run(() =>
            {
                var ok = SetClipboardDib(hwnd, dib);
                Plugin.Framework.RunOnTick(() =>
                {
                    if (ok) Plugin.ChatGui.Print("[TCRanking] 截圖已複製到剪貼簿（可直接 Ctrl+V 貼上）");
                    else    Plugin.ChatGui.PrintError("[TCRanking] 截圖複製剪貼簿失敗，請稍後再試");
                });
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[TCRanking] 截圖失敗");
            Plugin.ChatGui.PrintError("[TCRanking] 截圖失敗，詳見 /xllog");
        }
    }

    // 把 24bpp Bitmap 打包成 CF_DIB（BITMAPINFOHEADER + bottom-up BGR 像素）
    private static byte[] BuildDib(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride  = data.Stride;          // GDI+ 已 4-byte 對齊
            var imgSize = stride * h;
            var dib     = new byte[40 + imgSize];
            BitConverter.GetBytes(40).CopyTo(dib, 0);            // biSize
            BitConverter.GetBytes(w).CopyTo(dib, 4);             // biWidth
            BitConverter.GetBytes(h).CopyTo(dib, 8);             // biHeight（正值 = bottom-up）
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);     // biPlanes
            BitConverter.GetBytes((short)24).CopyTo(dib, 14);    // biBitCount
            BitConverter.GetBytes(imgSize).CopyTo(dib, 20);      // biSizeImage（biCompression=0 保持）
            // GDI+ 為 top-down，DIB 需 bottom-up → 逐列反向複製
            var scan0 = (nint)data.Scan0;
            for (var y = 0; y < h; y++)
                Marshal.Copy(scan0 + y * stride, dib, 40 + (h - 1 - y) * stride, stride);
            return dib;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static bool SetClipboardDib(nint hwnd, byte[] dib)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)dib.Length);
        if (hMem == nint.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero) { GlobalFree(hMem); return false; }
        Marshal.Copy(dib, 0, ptr, dib.Length);
        GlobalUnlock(hMem);

        // OpenClipboard 偶爾被其他程式短暫占用，重試數次
        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(hwnd)); i++)
            System.Threading.Thread.Sleep(10);
        if (!opened) { GlobalFree(hMem); return false; }
        try
        {
            EmptyClipboard();
            // 成功後記憶體由系統接管，不可再 free；失敗才由我們釋放
            if (SetClipboardData(CF_DIB, hMem) == nint.Zero) { GlobalFree(hMem); return false; }
            return true;
        }
        finally { CloseClipboard(); }
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
