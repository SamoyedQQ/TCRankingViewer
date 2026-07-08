using Dalamud.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace TCRankingViewer;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // DPAPI 加密儲存，綁定至當前 Windows 使用者帳號，防止直接讀 config 檔案取得金鑰
    public byte[]? EncryptedLicenseKey { get; set; }

    public int  CacheRefreshMinutes { get; set; } = 1440;
    public bool ChatNotifyOnJoin    { get; set; } = true;
    public bool AutoOpenWindow      { get; set; } = false;
    public bool NotifyUnranked      { get; set; } = false;

    // ── 排名視窗顯示 / 截圖 ─────────────────────────────────────────────────────
    // 「無備註」的黑名單玩家視為未黑單（正常顯示、不標記），而非隱藏該列
    public bool IgnoreBlacklistNoNote { get; set; } = false;
    public bool MaskIdOnScreenshot    { get; set; } = false; // 截圖時把玩家名稱打碼

    // 招募面板為解析跨服玩家名稱會短暫開啟遊戲「冒險者銘牌」再急著隱藏，lag 時偶爾閃現。
    // 關閉此項後不再自動開卡，改只靠名稱快取 / 社群 CID 快取解析 → 完全零閃現，
    // 代價是少數跨服成員可能顯示「無法解析」。預設開啟以維持最完整的解析率。
    public bool AutoResolveViaCharaCard { get; set; } = true;

    // 使用者自選要在表格中額外顯示的次要指標欄（key 見 RankCells.OptionalColumns）。
    // 未列入者仍可在懸浮提示看到。預設顯示 GCD 與死亡數，讓新指標開箱即見。
    public List<string> ExtraColumns { get; set; } = ["GCD", "deaths"];

    // 回傳「合法且去重、依 OptionalColumns 固定順序」的欄位清單，避免舊設定殘留無效 key。
    public List<string> GetExtraColumns()
    {
        var set = new HashSet<string>(ExtraColumns);
        return RankCells.OptionalColumns
            .Where(c => set.Contains(c.Key))
            .Select(c => c.Key)
            .ToList();
    }

    // ── 社群資料同步（需同意使用須知）──────────────────────────────────────────
    // 預設為 false：首次載入插件不自動觸發任何上傳/下載，給使用者選擇是否參與的權利
    public bool AutoSyncOnStartup { get; set; } = false;
    // CID 快取：ContentId→玩家名稱，屬遊戲內公開資料
    public bool UploadCidCache  { get; set; } = true;  // 上傳本機 CID 快取至 server
    public bool SyncCidCache    { get; set; } = true;  // 從 server 下載其他用戶貢獻的 CID 快取
    // 黑名單：玩家名稱（含備註），僅上傳名稱本身，不含操作記錄
    public bool UploadBlacklist { get; set; } = true;  // 上傳本機黑名單至 server
    public bool SyncBlacklist   { get; set; } = true;  // 從 server 下載其他用戶貢獻的黑名單

    private string? _cachedKey;

    public string? GetLicenseKey()
    {
        if (_cachedKey != null) return _cachedKey;
        if (EncryptedLicenseKey == null || EncryptedLicenseKey.Length == 0) return null;
        try
        {
            var plain = ProtectedData.Unprotect(EncryptedLicenseKey, null, DataProtectionScope.CurrentUser);
            _cachedKey = Encoding.UTF8.GetString(plain);
            return _cachedKey;
        }
        catch
        {
            return null;
        }
    }

    public void SetLicenseKey(string key)
    {
        _cachedKey = key;
        EncryptedLicenseKey = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
    }

    public void ClearLicenseKey()
    {
        _cachedKey = null;
        EncryptedLicenseKey = null;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
