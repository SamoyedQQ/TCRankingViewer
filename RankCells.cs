using Dalamud.Interface;
using ImGuiNET;

namespace TCRankingViewer;

/// <summary>
/// 排名表格中「PR / rDPS% / 次要指標懸浮提示 / 報告連結」等儲存格的共用繪製，
/// 供主視窗、招募側欄、履歷視窗共用，確保三處呈現完全一致。
/// 呼叫端需先 TableSetColumnIndex 到對應欄再呼叫。
/// </summary>
public static class RankCells
{
    // PR：null（樣本 < 20）顯示「—」；小樣本淡化並附提示；否則依 PR 分段上色。
    public static void DrawPr(RankingEntry e)
    {
        if (e.Pr is not int pr) { ImGui.TextColored(RankColors.Dim, "—"); return; }
        ImGui.TextColored(RankColors.PrColor(pr, e.PrLowSample), pr.ToString());
        if (e.PrLowSample && ImGui.IsItemHovered())
            ImGui.SetTooltip("小樣本（該職業樣本 20~50 筆），位階僅供參考");
    }

    // rDPS%：達該職最高者幾成。不套 FFLogs 段色（那是名次百分位 PR 的語意，rDPS% 用色反而混淆），
    // 一律白色，僅 PR 欄保留 FFLogs 分級色。
    public static void DrawRdpsPct(RankingEntry e)
    {
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
        ("active", "作場%", 46f),
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
