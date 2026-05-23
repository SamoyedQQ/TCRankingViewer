using System.IO;
using System.Text.Json;
using System.Threading;

namespace TCRankingViewer;

/// <summary>
/// Persists ContentId → player name mappings to disk so that CharaCard lookups
/// can be skipped for players seen in previous sessions.
/// </summary>
public sealed class CidCache : IDisposable
{
    private readonly string _filePath;
    private Dictionary<string, string> _cache = new();
    private readonly object _lock = new();
    private Timer? _saveTimer;
    private volatile bool _dirty;

    public int Count => _cache.Count;

    public CidCache()
    {
        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        _filePath = Path.Combine(dir, "cid_cache.json");
        Load();
    }

    public void Set(ulong contentId, string name)
    {
        if (contentId == 0 || string.IsNullOrEmpty(name)) return;
        var key = contentId.ToString();
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existing) && existing == name) return;
            _cache[key] = name;
        }
        _dirty = true;
        ScheduleSave();
    }

    public bool TryGet(ulong contentId, out string? name)
    {
        lock (_lock)
            return _cache.TryGetValue(contentId.ToString(), out name);
    }

    // ── Server sync ───────────────────────────────────────────────────────────

    // 取得整份快取的副本，供上傳至 server
    public Dictionary<string, string> GetAll()
    {
        lock (_lock) return new(_cache);
    }

    // 合併 server 下載的共享快取（本機已有的 CID 不覆寫，只填補空缺）
    public void MergeServerEntries(Dictionary<string, string> serverEntries)
    {
        int added = 0;
        lock (_lock)
        {
            foreach (var (key, name) in serverEntries)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!_cache.ContainsKey(key))
                {
                    _cache[key] = name;
                    added++;
                }
            }
        }
        if (added > 0)
        {
            _dirty = true;
            ScheduleSave();
        }
        Plugin.Log.Info($"[CidCache] 合併 server 共享快取 +{added} 筆（本機 {_cache.Count} 筆）");
    }

    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 3000, Timeout.Infinite);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            Plugin.Log.Info($"[CidCache] 載入 {_cache.Count} 筆 CID 快取");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[CidCache] 載入失敗");
            _cache = new();
        }
    }

    private void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Dictionary<string, string> snapshot;
            lock (_lock) snapshot = new(_cache);
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_filePath, json);
            Plugin.Log.Debug($"[CidCache] 已儲存 {_cache.Count} 筆");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[CidCache] 儲存失敗");
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Save();
    }
}
