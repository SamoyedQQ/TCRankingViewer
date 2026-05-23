using System.IO;
using System.Text.Json;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Dalamud.Plugin.Services;

namespace TCRankingViewer;

// 側錄某玩家的伺服器與 ContentId，供上傳/匹配補強用；任一欄位可為 null
public sealed record BlacklistMetaInfo(string? World, string? Cid);

public sealed class BlacklistService : IDisposable
{
    // 本機黑名單（從檔案載入），格式支援 "名字 # 備註"
    private Dictionary<string, string> _localEntries = new(StringComparer.OrdinalIgnoreCase);
    // server 同步下載的共享黑名單中，不在本機清單的條目（供 ConfigWindow 表格顯示用）
    private Dictionary<string, string> _serverEntries = new(StringComparer.OrdinalIgnoreCase);
    // server 端有備註的所有條目（含本機已有的），供 GetNote fallback 使用
    private Dictionary<string, string> _serverNoteCache = new(StringComparer.OrdinalIgnoreCase);

    // ── 玩家中繼資料（World / ContentId）持久化於 blacklist_meta.json ─────────
    // - 用獨立 side-car 而非改 blacklist.txt 格式：使用者仍可手動編輯 .txt 不被影響
    // - 來源：遊戲內黑名單 UI、PartyList、CrossRealmParty、PartyFinder 觀察
    // - 用途：上傳到 server 時，為黑名單條目加上 world + cid 讓 admin 跨服精準辨識
    private Dictionary<string, BlacklistMetaInfo> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _metaLock = new();
    private string MetaFilePath { get; }
    private volatile bool _metaDirty;
    private Timer?        _metaSaveTimer;

    private readonly Timer _pollTimer;
    private DateTime _lastWriteTime;

    // Auto-open state
    private bool  _waiting;
    private bool  _addonHidden;
    private int   _waitFrames;
    private bool  _soundMuted;
    private uint  _savedSoundSystem;
    private uint  _savedSoundSe;
    private const int MaxWaitFrames = 600; // ~10 s at 60 fps

    public string FilePath { get; }
    public int Count       => _localEntries.Count;
    public int ServerCount => _serverEntries.Count;

    public BlacklistService()
    {
        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        FilePath     = Path.Combine(dir, "blacklist.txt");
        MetaFilePath = Path.Combine(dir, "blacklist_meta.json");

        Load();
        LoadMeta();
        _lastWriteTime = File.Exists(FilePath) ? File.GetLastWriteTime(FilePath) : DateTime.MinValue;

        // Poll every 2 s for file changes instead of FileSystemWatcher (not available in Dalamud sandbox)
        _pollTimer = new Timer(_ => PollFileChange(), null, 2000, 2000);

        Plugin.ClientState.Login += OnLogin;
        if (Plugin.ClientState.IsLoggedIn)
            ScheduleAutoOpen();
    }

    // ── Login / schedule ─────────────────────────────────────────────────────
    private void OnLogin() => ScheduleAutoOpen();

    private void ScheduleAutoOpen()
    {
        _ = Task.Delay(3000).ContinueWith(
            _ => Plugin.Framework.RunOnFrameworkThread(StartAutoOpen));
    }

    // ── Auto-open on framework thread ─────────────────────────────────────────
    private unsafe void StartAutoOpen()
    {
        if (_waiting) return;

        var agent = AgentBlacklist.Instance();
        if (agent == null) { Plugin.Log.Warning("[Blacklist] AgentBlacklist null"); return; }

        // If already open and data is loaded, just read it
        if (agent->IsAddonShown())
        {
            ReadAndMerge();
            return;
        }

        Plugin.Log.Debug("[Blacklist] 自動開啟 BlackList addon 以讀取資料");
        _waiting     = false;
        _addonHidden = false;
        _waitFrames  = 0;

        MuteSound();
        agent->ShowAddon();

        // Start polling
        _waiting = true;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    // ── Per-frame polling ─────────────────────────────────────────────────────
    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
            if (!_waiting) { StopWaiting(); return; }
            _waitFrames++;

            var agent = AgentBlacklist.Instance();
            if (agent == null) { StopWaiting(); return; }

            // As soon as the addon is visible, hide it (suppress UI)
            if (!_addonHidden && agent->IsAddonShown())
            {
                agent->HideAddon();
                _addonHidden = true;
                Plugin.Log.Debug("[Blacklist] 隱藏黑名單視窗");
            }

            // Wait a minimum of 5 frames before checking data
            if (_waitFrames < 5) return;

            // Try to read data every 5 frames once shown
            if (_waitFrames % 5 == 0)
            {
                var proxy = GetProxy();
                if (proxy != null && proxy->EntryCount > 0)
                {
                    ReadAndMerge(proxy);
                    StopWaiting();
                    return;
                }
            }

            if (_waitFrames > MaxWaitFrames)
            {
                Plugin.Log.Warning("[Blacklist] 等待資料超時");
                StopWaiting();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Blacklist] OnFrameworkUpdate 發生例外，強制停止等待");
            StopWaiting();
        }
    }

    private void StopWaiting()
    {
        _waiting = false;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        UnmuteSound();
    }

    // ── Sound muting ──────────────────────────────────────────────────────────
    private void MuteSound()
    {
        if (_soundMuted) return;
        try
        {
            bool muted = false;
            Plugin.GameConfig.System.TryGet("SoundSystem", out _savedSoundSystem);
            Plugin.GameConfig.System.TryGet("SoundSe",     out _savedSoundSe);
            if (_savedSoundSystem > 0) { Plugin.GameConfig.System.Set("SoundSystem", 0u); muted = true; }
            if (_savedSoundSe     > 0) { Plugin.GameConfig.System.Set("SoundSe",     0u); muted = true; }
            if (muted) _soundMuted = true;
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "[Blacklist] MuteSound failed"); }
    }

    private void UnmuteSound()
    {
        if (!_soundMuted) return;
        _soundMuted = false;
        try
        {
            if (_savedSoundSystem > 0) Plugin.GameConfig.System.Set("SoundSystem", _savedSoundSystem);
            if (_savedSoundSe     > 0) Plugin.GameConfig.System.Set("SoundSe",     _savedSoundSe);
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "[Blacklist] UnmuteSound failed"); }
    }

    // ── Data reading ──────────────────────────────────────────────────────────
    private void ReadAndMerge() => Plugin.Framework.RunOnFrameworkThread(
        () => { unsafe { var p = GetProxy(); if (p != null) ReadAndMerge(p); } });

    private unsafe void ReadAndMerge(InfoProxyBlacklist* proxy)
    {
        // 優先用 UI string array：可同時取得 PlayerNames + Homeworlds + Notes（同 index 對齊）
        // InfoProxy.BlockedCharacters 結構有 Name/Id/Flag — 其中 Id 即 ContentId，
        // 用 name 反查可同時補上 cid。string array 與 BlockedCharacters 不保證 index 對齊，
        // 因此用 name 當 bridge 較安全。
        var entries = new List<(string name, string note)>();

        // 先建 name → ContentId 對應表（從 InfoProxy）
        var nameToCid = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in proxy->BlockedCharacters)
        {
            var n = entry.Name.ToString();
            if (string.IsNullOrWhiteSpace(n)) continue;
            // FFXIVClientStructs 中此欄位為 Id，語意是 ContentId
            if (entry.Id != 0) nameToCid[n] = entry.Id;
        }

        var strArr = BlackListStringArray.Instance();
        if (strArr != null)
        {
            var names  = strArr->PlayerNames;
            var worlds = strArr->Homeworlds;
            var notes  = strArr->Notes;
            var len    = Math.Min(names.Length, Math.Min(worlds.Length, notes.Length));
            for (var i = 0; i < len; i++)
            {
                var name = names[i].ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var world = worlds[i].ToString() ?? "";
                var note  = notes[i].ToString()  ?? "";
                entries.Add((name, note));
                // 順手把 world / cid 寫入 meta 側錄，供下次上傳時帶上 server
                var cid = nameToCid.GetValueOrDefault(name, 0UL);
                RecordMeta(name, world, cid);
            }
        }
        else
        {
            // fallback：UI 端尚未填字串時，退回讀 InfoProxy（無備註與 world，但仍有 cid）
            foreach (var entry in proxy->BlockedCharacters)
            {
                var name = entry.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add((name, ""));
                RecordMeta(name, null, entry.Id);
            }
        }
        Plugin.Log.Info($"[Blacklist] 讀取遊戲黑名單 {entries.Count} 筆 (EntryCount={proxy->EntryCount}, cidKnown={nameToCid.Count})");
        MergeFromGame(entries);
    }

    private static unsafe InfoProxyBlacklist* GetProxy()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return null;
        var infoModule = uiModule->GetInfoModule();
        if (infoModule == null) return null;
        var proxies = infoModule->InfoProxies;
        if (proxies.Length <= 5) return null;
        return (InfoProxyBlacklist*)proxies[5].Value;
    }

    // ── File merge ────────────────────────────────────────────────────────────
    // 三類處理：
    //   1) 本機已有 name 且已有備註 → 保留本機（本機永遠優先）
    //   2) 本機已有 name 但無備註、遊戲端有備註 → 原地補上 "name # note"
    //   3) 本機完全沒有此 name → append 到新時間區段
    private void MergeFromGame(List<(string name, string note)> imported)
    {
        if (imported.Count == 0)
        {
            Plugin.Log.Info("[Blacklist] 遊戲黑名單為空，無需匯入");
            return;
        }

        // 同名取最後一個有 note 的（防止同 name 重覆出現時備註被空字串覆蓋）
        var importedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, note) in imported)
        {
            if (!importedMap.TryGetValue(name, out var prev) || (string.IsNullOrEmpty(prev) && !string.IsNullOrEmpty(note)))
                importedMap[name] = note ?? "";
        }

        var lines    = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : new List<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated  = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            var (name, note) = ParseLine(trimmed);
            if (string.IsNullOrEmpty(name)) continue;
            existing.Add(name);

            // 補備註：本機無備註且遊戲端有
            if (string.IsNullOrEmpty(note)
                && importedMap.TryGetValue(name, out var gameNote)
                && !string.IsNullOrEmpty(gameNote))
            {
                lines[i] = $"{name} # {gameNote}";
                updated++;
            }
        }

        var toAdd = importedMap.Where(kv => !existing.Contains(kv.Key)).ToList();

        if (toAdd.Count == 0 && updated == 0)
        {
            Plugin.Log.Info($"[Blacklist] 遊戲黑名單 {imported.Count} 筆，無新增亦無備註更新");
            return;
        }

        if (toAdd.Count > 0)
        {
            // 與前面內容間隔一空行（若上一行已是空行則略過）
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add($"# 遊戲黑名單匯入 {DateTime.Now:yyyy-MM-dd HH:mm}");
            foreach (var (name, note) in toAdd)
                lines.Add(string.IsNullOrEmpty(note) ? name : $"{name} # {note}");
        }

        File.WriteAllLines(FilePath, lines);
        Plugin.Log.Info($"[Blacklist] 新增 {toAdd.Count} 筆、補備註 {updated} 筆（遊戲黑名單共 {imported.Count} 筆）");
        Load();
    }

    // ── File poll ─────────────────────────────────────────────────────────────
    private void PollFileChange()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var wt = File.GetLastWriteTime(FilePath);
            if (wt == _lastWriteTime) return;
            _lastWriteTime = wt;
            Load();
        }
        catch { /* ignore transient IO errors during poll */ }
    }

    private void Load()
    {
        try
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var (name, note) = ParseLine(line.Trim());
                    if (!string.IsNullOrEmpty(name))
                        entries[name] = note;
                }
            }
            _localEntries = entries;
            Plugin.Log.Info($"[Blacklist] 載入 {entries.Count} 筆");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Blacklist] 載入失敗");
        }
    }

    // 解析一行：開頭 # 是整行註解；否則 '#' 之前為名字，之後為備註
    private static (string name, string note) ParseLine(string trimmedLine)
    {
        if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
            return ("", "");
        var idx = trimmedLine.IndexOf('#');
        if (idx < 0)
            return (trimmedLine.Trim(), "");
        return (trimmedLine[..idx].Trim(), trimmedLine[(idx + 1)..].Trim());
    }

    // ── 查詢 ──────────────────────────────────────────────────────────────────
    public bool IsBlacklisted(string name)
        => _localEntries.ContainsKey(name) || _serverEntries.ContainsKey(name);

    // 取得備註：本機備註優先，其次 server 備註（含本機已有但無備註的條目）
    public string? GetNote(string name)
    {
        if (_localEntries.TryGetValue(name, out var local) && local.Length > 0)
            return local;
        if (_serverNoteCache.TryGetValue(name, out var cached) && cached.Length > 0)
            return cached;
        return null;
    }

    // ── Server sync ───────────────────────────────────────────────────────────

    // 取得所有本機黑名單條目，供上傳至 server；若 meta 中有 world/cid 一併帶上
    public IEnumerable<BlacklistEntry> GetAllLocalEntries()
        => _localEntries.Select(kvp =>
        {
            BlacklistMetaInfo? meta;
            lock (_metaLock) _meta.TryGetValue(kvp.Key, out meta);
            return new BlacklistEntry(kvp.Key, kvp.Value, meta?.World, meta?.Cid);
        });

    // ── Meta 中繼資料：World / ContentId 觀察與持久化 ────────────────────────

    // 任何時機（遊戲黑名單匯入、Party 觀察、PF 觀察）發現某玩家的 world / cid 都呼叫此 API
    // 只在玩家確實在本機黑名單上時才寫入，避免 meta 無限膨脹
    // 已存在的非空欄位不會被覆蓋（先到先贏，避免 stale 資料）
    public void RecordMeta(string name, string? world, ulong contentId)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_localEntries.ContainsKey(name)) return;

        var w = string.IsNullOrWhiteSpace(world) ? null : world!.Trim();
        var c = contentId == 0 ? null : contentId.ToString();
        if (w == null && c == null) return;

        lock (_metaLock)
        {
            _meta.TryGetValue(name, out var prev);
            var merged = new BlacklistMetaInfo(
                World: prev?.World ?? w,
                Cid:   prev?.Cid   ?? c);
            if (prev != null && prev.World == merged.World && prev.Cid == merged.Cid) return;
            _meta[name] = merged;
        }
        _metaDirty = true;
        ScheduleMetaSave();
    }

    private void ScheduleMetaSave()
    {
        _metaSaveTimer?.Dispose();
        _metaSaveTimer = new Timer(_ => SaveMeta(), null, 3000, Timeout.Infinite);
    }

    private void LoadMeta()
    {
        try
        {
            if (!File.Exists(MetaFilePath)) return;
            var json   = File.ReadAllText(MetaFilePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, BlacklistMetaInfo>>(json);
            if (parsed == null) return;
            lock (_metaLock)
                _meta = new Dictionary<string, BlacklistMetaInfo>(parsed, StringComparer.OrdinalIgnoreCase);
            Plugin.Log.Info($"[Blacklist] 載入 meta 中繼資料 {_meta.Count} 筆");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Blacklist] meta 載入失敗");
        }
    }

    private void SaveMeta()
    {
        if (!_metaDirty) return;
        _metaDirty = false;
        try
        {
            Dictionary<string, BlacklistMetaInfo> snapshot;
            lock (_metaLock) snapshot = new(_meta);
            File.WriteAllText(MetaFilePath, JsonSerializer.Serialize(snapshot));
            Plugin.Log.Debug($"[Blacklist] meta 已儲存 {snapshot.Count} 筆");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Blacklist] meta 儲存失敗");
        }
    }

    // 取得 server 同步下來的共享黑名單條目,供設定頁顯示「server 上有哪些人」
    public IEnumerable<BlacklistEntry> GetAllServerEntries()
        => _serverEntries.Select(kvp => new BlacklistEntry(kvp.Key, kvp.Value));

    // 合併 server 下載的共享黑名單（本機條目永遠優先，不覆寫）
    public void MergeServerEntries(List<BlacklistEntry> serverList)
    {
        var merged    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var noteCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in serverList)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            // 有備註的條目全部放進 noteCache，供 GetNote fallback（含本機已有但無備註者）
            if (!string.IsNullOrEmpty(entry.Note))
                noteCache[entry.Name] = entry.Note;
            // 本機已有的不放進顯示清單，避免重複
            if (!_localEntries.ContainsKey(entry.Name))
                merged[entry.Name] = entry.Note ?? "";
        }
        _serverEntries  = merged;
        _serverNoteCache = noteCache;
        Plugin.Log.Info($"[Blacklist] 合併 server 共享黑名單 {merged.Count} 筆（本機 {_localEntries.Count} 筆不受影響）");
    }

    public void RefreshFromGame() =>
        Plugin.Framework.RunOnFrameworkThread(StartAutoOpen);

    public void Dispose()
    {
        Plugin.ClientState.Login -= OnLogin;
        if (_waiting) Plugin.Framework.Update -= OnFrameworkUpdate;
        UnmuteSound();
        _pollTimer.Dispose();
        _metaSaveTimer?.Dispose();
        SaveMeta(); // 退出前 flush，避免最後幾秒的觀察結果遺失
    }
}
