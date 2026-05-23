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
