using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace TCRankingViewer;

public class ConfigWindow : Window, IDisposable
{
    private string _licenseKeyBuf      = "";
    private string _serverBlSearchBuf  = "";
    private float  _uiScaleDraft;   // 縮放滑桿拖曳中的暫存值，放開才套用（見 DrawDisplayTab）

    public ConfigWindow() : base(
        // 不用 AlwaysAutoResize：各分頁內容高矮不一，會讓視窗切分頁時忽大忽小。
        // 改固定尺寸（可由使用者自行拉伸並記住），分頁切換時大小維持一致；內容超出則視窗內捲動。
        "TC排名查詢 設定##TCRankCfgWin",
        ImGuiWindowFlags.None)
    {
        Size          = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(1000, 1200),
        };
    }

    public override void OnOpen()
    {
        _licenseKeyBuf     = "";
        _serverBlSearchBuf = "";
        _uiScaleDraft      = Plugin.Configuration.UiScale;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(1, 0.85f, 0.3f, 1), "TC Savage 排名查詢 設定");
        ImGui.Separator();

        // 分頁化：原本一長串設定拆成分類分頁，最常調整的「顯示」放第一個。
        if (!ImGui.BeginTabBar("##cfgTabs")) return;

        if (ImGui.BeginTabItem("顯示"))          { DrawDisplayTab();   ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("金鑰"))          { DrawLicenseTab();   ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("社群 / 黑名單")) { DrawCommunityTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("資料 / 狀態"))   { DrawDataTab();      ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    // ── 顯示分頁（最常用）：整體縮放、排名視窗顯示選項、額外欄位、通知 ─────────────
    private void DrawDisplayTab()
    {
        var cfg = Plugin.Configuration;
        ImGui.Spacing();

        // 整體 UI 縮放（字體＋icon）
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "外觀");
        ImGui.Spacing();
        // 拖曳滑桿只更新暫存值；放開滑桿才寫回設定並觸發字型重建。
        // 因為切換倍率會重建字型圖集（成本較高），逐幀重建會嚴重卡頓，故只在放開時套用一次。
        ImGui.SetNextItemWidth(220);
        ImGui.SliderFloat("整體字體 / icon 大小", ref _uiScaleDraft, 0.8f, 2.0f, "%.2fx");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("同時放大／縮小主視窗、招募、履歷三個視窗的文字與職業 icon\n（放開滑桿後套用）");
        if (ImGui.IsItemDeactivatedAfterEdit() && Math.Abs(_uiScaleDraft - cfg.UiScale) > 0.001f)
        { cfg.UiScale = _uiScaleDraft; cfg.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "排名視窗顯示");
        ImGui.Spacing();

        var ignoreBlNoNote = cfg.IgnoreBlacklistNoNote;
        if (ImGui.Checkbox("無備註的黑名單玩家當作未黑單（不標記）", ref ignoreBlNoNote))
        { cfg.IgnoreBlacklistNoNote = ignoreBlNoNote; cfg.Save(); }

        var maskId = cfg.MaskIdOnScreenshot;
        if (ImGui.Checkbox("截圖時隱藏玩家名稱（打碼）", ref maskId))
        { cfg.MaskIdOnScreenshot = maskId; cfg.Save(); }

        var autoCard = cfg.AutoResolveViaCharaCard;
        if (ImGui.Checkbox("招募面板自動開卡解析跨服名稱（可能偶爾閃現角色卡）", ref autoCard))
        { cfg.AutoResolveViaCharaCard = autoCard; cfg.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "關閉後完全不再閃現角色卡；少數跨服成員可能顯示「無法解析」\n"
              + "（同隊過或社群 CID 快取通常已補齊，多數情況仍能正常顯示）。");

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "額外顯示欄位（未勾選者仍可在懸浮提示看到）");
        ImGui.Spacing();
        var extra = new HashSet<string>(cfg.ExtraColumns);
        var changed = false;
        for (var i = 0; i < RankCells.OptionalColumns.Length; i++)
        {
            var (key, header, _) = RankCells.OptionalColumns[i];
            var on = extra.Contains(key);
            if (ImGui.Checkbox($"{header}##col_{key}", ref on))
            {
                if (on) extra.Add(key); else extra.Remove(key);
                changed = true;
            }
            // 每行擺三個，排版較緊湊
            if (i % 3 != 2 && i != RankCells.OptionalColumns.Length - 1) ImGui.SameLine(0, 24);
        }
        if (changed)
        {
            // 依 OptionalColumns 固定順序寫回，維持欄位順序一致
            cfg.ExtraColumns = RankCells.OptionalColumns
                .Where(c => extra.Contains(c.Key)).Select(c => c.Key).ToList();
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "通知");
        ImGui.Spacing();

        var chatNotify = cfg.ChatNotifyOnJoin;
        if (ImGui.Checkbox("隊友加入時，在聊天框顯示排名通知", ref chatNotify))
        { cfg.ChatNotifyOnJoin = chatNotify; cfg.Save(); }

        var autoOpen = cfg.AutoOpenWindow;
        if (ImGui.Checkbox("隊友加入時，自動開啟排名視窗", ref autoOpen))
        { cfg.AutoOpenWindow = autoOpen; cfg.Save(); }

        var notifyUnranked = cfg.NotifyUnranked;
        if (ImGui.Checkbox("未上榜的隊友也顯示通知", ref notifyUnranked))
        { cfg.NotifyUnranked = notifyUnranked; cfg.Save(); }
    }

    // ── 金鑰分頁 ────────────────────────────────────────────────────────────────
    private void DrawLicenseTab()
    {
        var cfg = Plugin.Configuration;
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "許可證金鑰");
        ImGui.Spacing();

        if (cfg.GetLicenseKey() != null)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1), "✓ 已設定");
        else
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1), "✗ 尚未設定");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(340);
        ImGui.InputText("##licenseKey", ref _licenseKeyBuf, 64, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("儲存##lk"))
        {
            if (!string.IsNullOrWhiteSpace(_licenseKeyBuf))
            {
                cfg.SetLicenseKey(_licenseKeyBuf.Trim());
                cfg.Save();
                _licenseKeyBuf = "";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("清除##lk"))
        {
            _licenseKeyBuf = "";
            cfg.ClearLicenseKey();
            cfg.Save();
        }
    }

    // ── 社群 / 黑名單分頁 ────────────────────────────────────────────────────────
    private void DrawCommunityTab()
    {
        var cfg = Plugin.Configuration;
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "社群資料同步");
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.4f, 1));
        ImGui.TextWrapped("使用須知：啟用同步時，您的 CID 快取（角色 ID→名稱對應）或黑名單將上傳至共享伺服器供其他插件使用者下載。這些資料屬遊戲內公開可見資訊，但請確認知悉後再開啟。");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var autoSync = cfg.AutoSyncOnStartup;
        if (ImGui.Checkbox("載入插件時自動同步社群（未勾選則僅在按下「立即同步」時觸發）", ref autoSync))
        { cfg.AutoSyncOnStartup = autoSync; cfg.Save(); }

        ImGui.Spacing();

        var uploadCid = cfg.UploadCidCache;
        if (ImGui.Checkbox("上傳本機 CID 快取至 server（貢獻角色 ID→名稱對應）", ref uploadCid))
        { cfg.UploadCidCache = uploadCid; cfg.Save(); }

        var syncCid = cfg.SyncCidCache;
        if (ImGui.Checkbox("從 server 同步其他用戶的 CID 快取（補全本機未知角色名稱）", ref syncCid))
        { cfg.SyncCidCache = syncCid; cfg.Save(); }

        ImGui.Spacing();

        var uploadBl = cfg.UploadBlacklist;
        if (ImGui.Checkbox("上傳本機黑名單至 server（含備註，共享封鎖名單）", ref uploadBl))
        { cfg.UploadBlacklist = uploadBl; cfg.Save(); }

        var syncBl = cfg.SyncBlacklist;
        if (ImGui.Checkbox("從 server 同步其他用戶的黑名單（在排行榜標記共享封鎖玩家）", ref syncBl))
        { cfg.SyncBlacklist = syncBl; cfg.Save(); }

        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1),
            $"本機 CID 快取 {Plugin.CidCache.Count} 筆  ·  共享黑名單（server）{Plugin.BlacklistService.ServerCount} 筆");

        ImGui.Spacing();

        if (Plugin.IsSyncing)
        {
            ImGui.BeginDisabled();
            ImGui.Button("同步中...");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("立即同步社群資料"))
        {
            _ = Plugin.TriggerSyncAsync();
        }
        if (!string.IsNullOrEmpty(Plugin.LastSyncMessage))
        {
            ImGui.SameLine();
            var msgColor = Plugin.LastSyncMessage!.StartsWith('✓')
                ? new Vector4(0.45f, 0.90f, 0.45f, 1)
                : Plugin.LastSyncMessage.StartsWith('✗')
                    ? new Vector4(0.95f, 0.40f, 0.40f, 1)
                    : new Vector4(0.55f, 0.55f, 0.55f, 1);
            ImGui.TextColored(msgColor, Plugin.LastSyncMessage);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "黑名單");
        ImGui.Spacing();
        var blTotal = Plugin.BlacklistService.ServerCount > 0
            ? $"本機 {Plugin.BlacklistService.Count} 筆 + server {Plugin.BlacklistService.ServerCount} 筆"
            : $"共 {Plugin.BlacklistService.Count} 筆";
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
            $"{blTotal}  ·  每行一個名字，# 開頭為整行註解，「名字 # 備註」格式可加備註，儲存後自動套用");
        var blPath = Plugin.BlacklistService.FilePath;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
        ImGui.InputText("##blpath", ref blPath, 512, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button("開啟##bl"))
        {
            try
            {
                if (!System.IO.File.Exists(blPath))
                    System.IO.File.WriteAllText(blPath,
                        "# 每行填入一個玩家名稱，儲存後自動生效\n# 可加備註：玩家名稱 # 備註文字\n# 例：\n# 草莓小蛋糕 # 喜歡搶奶\n");
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(blPath) { UseShellExecute = true });
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Blacklist] 開啟檔案失敗"); }
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"檢視 server 共享黑名單（{Plugin.BlacklistService.ServerCount} 筆）##srvBl"))
            DrawServerBlacklistTable();
    }

    // ── 資料 / 狀態分頁 ─────────────────────────────────────────────────────────
    private void DrawDataTab()
    {
        var cfg = Plugin.Configuration;
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1), "資料快取");
        ImGui.Spacing();

        var cacheMin = cfg.CacheRefreshMinutes;
        ImGui.SetNextItemWidth(130);
        if (ImGui.SliderInt("自動更新間隔（分鐘）", ref cacheMin, 60, 1440))
        { cfg.CacheRefreshMinutes = cacheMin; cfg.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
            $"狀態：{Plugin.RankingService.Status}");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1),
            $"共 {Plugin.RankingService.TotalPlayers} 位玩家 " +
            $"/ {Plugin.RankingService.TotalEntries} 筆條目");

        ImGui.Spacing();
        if (ImGui.Button("立即重新下載資料"))
        {
            _ = Plugin.RankingService.RefreshAsync(force: true)
                .ContinueWith(_ => Plugin.PartyWatcher.RebuildResults());
        }
    }

    // 顯示 server 同步下來的共享黑名單清單(可搜尋過濾,固定高度可滑動)
    private void DrawServerBlacklistTable()
    {
        var all = Plugin.BlacklistService.GetAllServerEntries().ToList();
        if (all.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1),
                "尚未同步或 server 上目前無共享條目（啟用「從 server 同步」並重新同步可載入）");
            return;
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##srvBlSearch", "搜尋名稱或備註...", ref _serverBlSearchBuf, 64);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1), $"共 {all.Count} 筆");

        var filtered = string.IsNullOrWhiteSpace(_serverBlSearchBuf)
            ? all
            : all.Where(e => e.Name.Contains(_serverBlSearchBuf, StringComparison.OrdinalIgnoreCase)
                          || (e.Note?.Contains(_serverBlSearchBuf, StringComparison.OrdinalIgnoreCase) ?? false))
                 .ToList();

        const ImGuiTableFlags flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        // 固定高度限制顯示區,內部捲動避免設定視窗無限長
        if (!ImGui.BeginTable("##srvBlTable", 2, flags, new Vector2(-1, 240)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("備註", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableHeadersRow();

        var dim = new Vector4(0.55f, 0.55f, 0.55f, 1);
        var red = new Vector4(0.95f, 0.40f, 0.40f, 1);
        foreach (var entry in filtered)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(red, entry.Name);
            ImGui.TableSetColumnIndex(1);
            if (string.IsNullOrEmpty(entry.Note))
                ImGui.TextColored(dim, "─");
            else
                ImGui.TextUnformatted(entry.Note);
        }

        ImGui.EndTable();
    }

    public void Dispose() { }
}
