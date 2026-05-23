using System.IO;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Dalamud.Plugin.Services;

namespace TCRankingViewer;

public sealed class BlacklistService : IDisposable
{
    // 本機黑名單（從檔案載入），格式支援 "名字 # 備註"
    private Dictionary<string, string> _localEntries = new(StringComparer.OrdinalIgnoreCase);
    // server 同步下載的共享黑名單中，不在本機清單的條目（供 ConfigWindow 表格顯示用）
    private Dictionary<string, string> _serverEntries = new(StringComparer.OrdinalIgnoreCase);
    // server 端有備註的所有條目（含本機已有的），供 GetNote fallback 使用
    private Dictionary<string, string> _serverNoteCache = new(StringComparer.OrdinalIgnoreCase);

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
        FilePath = Path.Combine(dir, "blacklist.txt");

        Load();
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
        // 優先用 UI string array：可同時取得 PlayerNames + Notes（同 index 對齊）
        // InfoProxy.BlockedCharacters 結構只有 Name/Id/Flag，沒有備註欄位
        var entries = new List<(string name, string note)>();
        var strArr  = BlackListStringArray.Instance();
        if (strArr != null)
        {
            var names = strArr->PlayerNames;
            var notes = strArr->Notes;
            var len   = Math.Min(names.Length, notes.Length);
            for (var i = 0; i < len; i++)
            {
                var name = names[i].ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add((name, notes[i].ToString() ?? ""));
            }
        }
        else
        {
            // fallback：UI 端尚未填字串時，退回讀 InfoProxy（無備註）
            foreach (var entry in proxy->BlockedCharacters)
            {
                var name = entry.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    entries.Add((name, ""));
            }
        }
        Plugin.Log.Info($"[Blacklist] 讀取遊戲黑名單 {entries.Count} 筆 (EntryCount={proxy->EntryCount})");
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

    // 取得所有本機黑名單條目，供上傳至 server
    public IEnumerable<BlacklistEntry> GetAllLocalEntries()
        => _localEntries.Select(kvp => new BlacklistEntry(kvp.Key, kvp.Value));

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
    }
}
