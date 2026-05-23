using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace TCRankingViewer;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Gold    = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 Silver  = new(0.80f, 0.80f, 0.85f, 1f);
    private static readonly Vector4 Bronze  = new(0.80f, 0.55f, 0.30f, 1f);
    private static readonly Vector4 Green   = new(0.45f, 0.90f, 0.45f, 1f);
    private static readonly Vector4 Red     = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Dim     = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 White   = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 Teal    = new(0.40f, 0.85f, 0.85f, 1f);

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

    private readonly HashSet<string> _expandedPlayers = new();
    private string _categoryFilter = "全部";

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
        DrawToolbar();
        ImGui.Separator();
        DrawCategoryTabs();
        DrawStatus();
        ImGui.Separator();
        DrawTable();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("重新整理隊伍"))
            Plugin.PartyWatcher.RebuildResults();

        ImGui.SameLine();

        if (Plugin.RankingService.IsLoading)
        {
            ImGui.BeginDisabled();
            ImGui.Button("下載中...");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("重新下載資料"))
        {
            _ = Plugin.RankingService.RefreshAsync(force: true)
                .ContinueWith(_ => Plugin.PartyWatcher.RebuildResults());
        }

        ImGui.SameLine();
        if (ImGui.Button("重整黑名單"))
            Plugin.BlacklistService.RefreshFromGame();

        ImGui.SameLine();
        if (ImGui.Button("設定"))
            Plugin.ConfigWindow.IsOpen = true;
    }

    private void DrawCategoryTabs()
    {
        if (!ImGui.BeginTabBar("##catFilter")) return;

        if (ImGui.BeginTabItem("全部"))    { _categoryFilter = "全部";  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("零式"))    { _categoryFilter = "零式";  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("絕境戰"))  { _categoryFilter = "絕";    ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("極 / 幻")) { _categoryFilter = "極/幻"; ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    private static void DrawStatus()
    {
        var color = Plugin.RankingService.IsReady ? Green : Red;
        ImGui.TextColored(color, Plugin.RankingService.Status);
    }

    private void DrawTable()
    {
        var results = Plugin.PartyWatcher.CurrentResults;

        if (results.Count == 0)
        {
            ImGui.TextColored(Dim, "目前沒有隊伍成員。請進入隊伍後點「重新整理隊伍」。");
            return;
        }

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders     |
            ImGuiTableFlags.RowBg       |
            ImGuiTableFlags.ScrollY     |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##ranks", 6, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("玩家名稱", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("職業",     ImGuiTableColumnFlags.WidthFixed,   52f);
        ImGui.TableSetupColumn("副本",     ImGuiTableColumnFlags.WidthStretch, 3.0f);
        ImGui.TableSetupColumn("排名",     ImGuiTableColumnFlags.WidthFixed,   60f);
        ImGui.TableSetupColumn("rDPS",     ImGuiTableColumnFlags.WidthFixed,   78f);
        ImGui.TableSetupColumn("aDPS",     ImGuiTableColumnFlags.WidthFixed,   78f);
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

            var isBlacklisted = Plugin.BlacklistService.IsBlacklisted(member.CharacterName);
            var nameColor = isBlacklisted ? Red : (member.IsSelf ? Gold : White);
            ImGui.TextColored(nameColor, (member.IsSelf ? "★ " : "") + member.CharacterName);
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

            if (isBlacklisted)
            {
                ImGui.SameLine();
                DrawBlacklistButton(member.CharacterName);
            }

            if (best == null)
            {
                ImGui.TableSetColumnIndex(1); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(2); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(Dim, member.IsFound ? "此類別無資料" : "未上榜");
                ImGui.TableSetColumnIndex(4); ImGui.TextColored(Dim, "─");
                ImGui.TableSetColumnIndex(5); ImGui.TextColored(Dim, "─");
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
        "極/幻" => entries.Where(e => e.Category is "極" or "幻").ToList(),
        _       => entries,
    };

    // 展開時輸出分類區塊（僅在「全部」模式使用）
    private void DrawCategoryRows(List<RankingEntry> entries)
    {
        var groups = new (string label, Vector4 color, IEnumerable<RankingEntry> rows)[]
        {
            ("絕境戰", Gold,   entries.Where(e => e.Category == "絕"  && !e.IsObsolete)),
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
            ImGui.TextColored(color, $"  ▸ {label}");
            for (var c = 1; c <= 5; c++)
            { ImGui.TableSetColumnIndex(c); ImGui.TextColored(Dim, ""); }

            foreach (var e in sorted)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawEntryColumns(e);
            }
        }
    }

    private void DrawEntryColumns(RankingEntry e)
    {
        var abbrev = string.IsNullOrEmpty(e.Job) ? "─" : JobAbbrev.Get(e.Job);
        var jColor = (e.IsObsolete || e.IsProg) ? Dim
            : (JobColors.TryGetValue(abbrev, out var jc) ? jc : Dim);

        ImGui.TableSetColumnIndex(1); ImGui.TextColored(jColor, abbrev);

        if (e.IsProg)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(Dim, ShortenBossName(e.Boss));
            ImGui.SameLine();
            ImGui.TextColored(Teal, "未通關");
            var progLabel = e.PhaseNumber > 0 ? $"P{e.PhaseNumber}" : "進度中";
            ImGui.TableSetColumnIndex(3); ImGui.TextColored(Teal, progLabel);
            ImGui.TableSetColumnIndex(4); ImGui.TextColored(Dim, "─");
            ImGui.TableSetColumnIndex(5); ImGui.TextColored(Dim, "─");
        }
        else
        {
            ImGui.TableSetColumnIndex(2); ImGui.TextColored(Dim, ShortenBossName(e.Boss));
            ImGui.TableSetColumnIndex(3); ImGui.TextColored(e.IsObsolete ? Dim : RankColor(e.Rank), $"#{e.Rank}");
            ImGui.TableSetColumnIndex(4); ImGui.TextColored(e.IsObsolete ? Dim : White, $"{e.Rdps:F0}");
            ImGui.TableSetColumnIndex(5); ImGui.TextColored(Dim, $"{e.Adps:F0}");
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
        if (ImGui.SmallButton($"✕##{popupId}"))
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
