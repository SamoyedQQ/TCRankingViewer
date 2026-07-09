using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using ImGuiNET;

namespace TCRankingViewer;

/// <summary>
/// 排名表格中「PR / rDPS% / 次要指標懸浮提示 / 報告連結」等儲存格的共用繪製，
/// 供主視窗、招募側欄、履歷視窗共用，確保三處呈現完全一致。
/// 呼叫端需先 TableSetColumnIndex 到對應欄再呼叫。
/// </summary>
public static class RankCells
{
    // 職業 icon 的預設邊長：貼齊整列框高（GetFrameHeight），icon 填滿列高、上下置中不歪，
    // 也與招募視窗放大的「現職」icon 同尺寸，三視窗大小一致。
    public static float JobIconSize => ImGui.GetFrameHeight();

    // icon 欄的建議欄寬：icon 邊長 + 兩側儲存格內距，確保放大後的 icon 不被裁切。
    public static float JobIconColumnWidth => ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.X * 2f;

    // 儲存格文字：先對齊到 frame padding 再輸出，讓文字在「因 icon 撐成 frame 高度」的列裡
    // 上下置中，不會靠上飄起（與同列的職業 icon、箭頭按鈕對齊）。表格內的文字一律走這裡。
    public static void CellText(Vector4 color, string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, text);
    }

    // jobId → 三碼英文縮寫，對應 assets/jobs/<縮寫>.png 打包圖檔（見 assets/jobs/README.txt）
    private static readonly Dictionary<byte, string> JobCode = new()
    {
        // 進階職（排名戰績只會是這些）
        { 19, "PLD" }, { 21, "WAR" }, { 32, "DRK" }, { 37, "GNB" },
        { 24, "WHM" }, { 28, "SCH" }, { 33, "AST" }, { 40, "SGE" },
        { 20, "MNK" }, { 22, "DRG" }, { 30, "NIN" }, { 34, "SAM" }, { 39, "RPR" }, { 41, "VPR" },
        { 23, "BRD" }, { 31, "MCH" }, { 38, "DNC" },
        { 25, "BLM" }, { 27, "SMN" }, { 35, "RDM" }, { 42, "PCT" },
        // 生產（DoH）／採集（DoL）
        { 8, "CRP" }, { 9, "BSM" }, { 10, "ARM" }, { 11, "GSM" },
        { 12, "LTW" }, { 13, "WVR" }, { 14, "ALC" }, { 15, "CUL" },
        { 16, "MIN" }, { 17, "BTN" }, { 18, "FSH" },
        // 基礎職＋青魔：不會出現在排名，但招募「現職」欄可能遇到
        { 1, "GLA" }, { 2, "PGL" }, { 3, "MRD" }, { 4, "LNC" }, { 5, "ARC" },
        { 6, "CNJ" }, { 7, "THM" }, { 26, "ACN" }, { 29, "ROG" }, { 36, "BLU" },
    };

    // 打包職業圖檔所在資料夾（插件 dll 同層的 jobs/），首次存取時算出並快取
    private static string? _jobsDir;
    private static string JobsDir => _jobsDir ??= Path.Combine(
        Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? "", "jobs");

    // 職業 icon 儲存格（風格同 FFLogsViewer）：優先讀 assets/jobs/<縮寫>.png 高解析圖檔，
    // 缺檔則回退遊戲內建 icon（62100 + ClassJob RowId），查不到職業則留白佔位。
    // dim=true（過版本、練習中等非正式戰績）時以半透明呈現，維持原本文字淡化的語意。
    // show=false 時只佔一個等大空位（供「同職業連續列只顯示第一個」的去重用）。
    // size>0 可指定放大尺寸（如招募視窗把現職 icon 放大到列高）。
    public static void DrawJobIcon(string job, bool dim = false, bool show = true, float size = 0f)
    {
        var sz = new Vector2(size > 0f ? size : JobIconSize);
        if (!show) { ImGui.Dummy(sz); return; }

        var jobId = JobAbbrev.GetJobId(job);
        if (jobId == 0) { ImGui.Dummy(sz); return; }

        // 先試打包的高解析圖檔；缺檔才回退遊戲內建 icon
        nint handle = 0;
        if (JobCode.TryGetValue(jobId, out var code))
        {
            var path = Path.Combine(JobsDir, code + ".png");
            if (File.Exists(path))
                handle = Plugin.TextureProvider.GetFromFile(path).GetWrapOrEmpty().ImGuiHandle;
        }
        if (handle == 0)
            handle = Plugin.TextureProvider
                .GetFromGameIcon(new GameIconLookup(62100u + jobId)).GetWrapOrEmpty().ImGuiHandle;

        var tint = dim ? new Vector4(1f, 1f, 1f, 0.4f) : Vector4.One;
        ImGui.Image(handle, sz, Vector2.Zero, Vector2.One, tint);
    }

    // PR：null（樣本 < 20）顯示「—」；小樣本淡化並附提示；否則依 PR 分段上色。
    public static void DrawPr(RankingEntry e)
    {
        ImGui.AlignTextToFramePadding();
        if (e.Pr is not int pr) { ImGui.TextColored(RankColors.Dim, "—"); return; }
        ImGui.TextColored(RankColors.PrColor(pr, e.PrLowSample), pr.ToString());
        if (e.PrLowSample && ImGui.IsItemHovered())
            ImGui.SetTooltip("小樣本（該職業樣本 20~50 筆），位階僅供參考");
    }

    // rDPS%：達該職最高者幾成。不套 FFLogs 段色（那是名次百分位 PR 的語意，rDPS% 用色反而混淆），
    // 一律白色，僅 PR 欄保留 FFLogs 分級色。
    public static void DrawRdpsPct(RankingEntry e)
    {
        ImGui.AlignTextToFramePadding();
        if (e.RdpsPct is not double pct) { ImGui.TextColored(RankColors.Dim, "—"); return; }
        ImGui.TextColored(RankColors.White, $"{pct:F1}");
    }

    // ── 可自選的次要指標欄（設定頁勾選；主視窗/招募/履歷共用）──────────────────────
    // Key 為設定儲存值；Header 為表頭；Width 為固定欄寬。
    public static readonly (string Key, string Header, float Width)[] OptionalColumns =
    [
        ("aDPS",   "aDPS", 62f),
        ("nDPS",   "nDPS", 62f),
        ("GCD",    "GCD",  48f),
        ("uptime", "GCD%", 46f),
        ("active", "Active%", 54f),
        ("deaths", "死亡", 40f),
    ];

    public static string OptionalHeader(string key)
    {
        foreach (var c in OptionalColumns) if (c.Key == key) return c.Header;
        return key;
    }

    public static float OptionalWidth(string key)
    {
        foreach (var c in OptionalColumns) if (c.Key == key) return c.Width;
        return 56f;
    }

    // 繪製單一可選欄儲存格：無值 / 進度條目顯示「—」。
    public static void DrawOptionalCell(RankingEntry e, string key)
    {
        ImGui.AlignTextToFramePadding();
        switch (key)
        {
            case "aDPS":
                ImGui.TextColored(e.IsProg ? RankColors.Dim : RankColors.White,
                    e.IsProg ? "—" : $"{e.Adps:F0}");
                break;
            case "nDPS":
                if (e.Ndps is double nd) ImGui.TextColored(RankColors.White, $"{nd:F0}");
                else ImGui.TextColored(RankColors.Dim, "—");
                break;
            case "GCD":
                if (e.GcdMs is int gcd) ImGui.TextColored(RankColors.White, $"{gcd / 1000.0:F2}");
                else ImGui.TextColored(RankColors.Dim, "—");
                break;
            case "uptime":
                if (e.GcdUptime is double gu) ImGui.TextColored(RankColors.White, $"{gu:F1}%%");
                else ImGui.TextColored(RankColors.Dim, "—");
                break;
            case "active":
                if (e.ActivePercent is double ap) ImGui.TextColored(RankColors.White, $"{ap:F1}%%");
                else ImGui.TextColored(RankColors.Dim, "—");
                break;
            case "deaths":
                if (e.Deaths is int d)
                    ImGui.TextColored(d == 0 ? RankColors.Green : RankColors.White, d.ToString());
                else ImGui.TextColored(RankColors.Dim, "—");
                break;
            default:
                ImGui.TextColored(RankColors.Dim, "—");
                break;
        }
    }

    // 副本名儲存格的互動：次要指標已可用「額外顯示欄位」呈現，故懸浮提示只保留報告連結說明；
    // 有 FFLogs 報告時提示可點、游標變手指，左鍵點副本名即開啟報告（tooltip 內無法直接點連結）。
    // 呼叫端需在剛畫完副本名後立即呼叫。
    public static void DrawSecondaryTooltip(RankingEntry e)
    {
        if (string.IsNullOrEmpty(e.ReportCode)) return;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("左鍵點副本名開啟 FFLogs 報告");
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (ImGui.IsItemClicked())
            OpenReport(e.ReportCode!);
    }

    // 報告連結儲存格：有 report_code 才顯示可點的外部連結圖示（FontAwesome，保證能顯示），
    // 點擊以系統瀏覽器開啟 FFLogs 報告。
    public static void DrawReportLink(RankingEntry e)
    {
        ImGui.AlignTextToFramePadding();
        if (string.IsNullOrEmpty(e.ReportCode)) { ImGui.TextColored(RankColors.Dim, "—"); return; }
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(RankColors.Teal, FontAwesomeIcon.ExternalLinkAlt.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("開啟 FFLogs 報告");
        }
        if (ImGui.IsItemClicked()) OpenReport(e.ReportCode!);
    }

    private static void OpenReport(string reportCode)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                RankColors.ReportUrl(reportCode)) { UseShellExecute = true });
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Report] 開啟報告連結失敗"); }
    }
}
