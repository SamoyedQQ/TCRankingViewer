using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCRankingViewer;

public class RankingService : IDisposable
{
    private const string WorkerBaseUrl = "https://api.tommy04166-a79.workers.dev";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private volatile Dictionary<string, List<RankingEntry>> _index = [];
    private volatile string  _status    = "尚未載入";
    private volatile bool    _loading   = false;
    private string?          _updated;
    private DateTime         _lastFetched = DateTime.MinValue;

    private readonly HttpClient    _http;
    private readonly SemaphoreSlim _sem = new(1, 1);

    public bool    IsReady      => _index.Count > 0;
    public bool    IsLoading    => _loading;
    public string  Status       => _status;
    public string? DataUpdated  => _updated;
    public int     TotalPlayers => _index.Count;
    public int     TotalEntries => _index.Values.Sum(v => v.Count);

    public RankingService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent",
            "TCRankingViewer/1.0 (Dalamud Plugin; contact via GitHub)");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ─── HMAC 認證 ──────────────────────────────────────────────────────────
    // UUID 不直接傳輸：以 sha256(uuid)[:16] 作為公開識別符，uuid 本身作為 HMAC 簽名金鑰
    private static string BuildHmacHeader(string licenseKey, string path)
    {
        var keyBytes = Encoding.UTF8.GetBytes(licenseKey);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var id = Convert.ToHexString(SHA256.HashData(keyBytes))[..16].ToLowerInvariant();
        using var hmac = new HMACSHA256(keyBytes);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}|{path}")));
        return $"HMAC {id}|{ts}|{sig}";
    }

    private async Task<string> GetWithAuthAsync(string licenseKey, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{WorkerBaseUrl}{path}");
        req.Headers.TryAddWithoutValidation("Authorization", BuildHmacHeader(licenseKey, path));
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    // ─── 下載 / 更新 ───────────────────────────────────────────────────────
    public async Task RefreshAsync(bool force = false)
    {
        if (!force && IsReady &&
            DateTime.UtcNow - _lastFetched <
            TimeSpan.FromMinutes(Plugin.Configuration.CacheRefreshMinutes))
            return;

        if (!await _sem.WaitAsync(0)) return;

        try
        {
            _loading = true;
            _status  = "正在下載資料...";

            var licenseKey = Plugin.Configuration.GetLicenseKey();
            if (licenseKey == null)
            {
                _status = "✗ 尚未設定許可證金鑰，請至設定頁面輸入";
                return;
            }

            Plugin.Log.Information("[TCRanking] 下載 encounters...");

            var encountersJson = await GetWithAuthAsync(licenseKey, "/encounters");

            var keys = ParseEncounterKeys(encountersJson);
            Plugin.Log.Information($"[TCRanking] 取得 {keys.Count} 個副本，並行下載排名...");
            _status = $"正在下載 {keys.Count} 個副本排名...";

            var rankingFiles = await Task.WhenAll(
                keys.Select(k => DownloadRankingFile(licenseKey, k)));

            _status = "正在解析...";
            var allEntries = new List<RankingEntry>();

            // Kantai235 資料：依 (encounter, job) 分組，rDPS desc 排序，client-side 計算 job_rank
            foreach (var file in rankingFiles)
            {
                if (file?.Entries == null || file.Encounter == null) continue;
                var bossName   = EncounterMeta.DisplayBossName(file.Encounter.Key, file.Encounter.Name);
                var category   = file.Encounter.Category;
                var isObsolete = EncounterMeta.IsObsoleteKey(file.Encounter.Key);

                var byJob = file.Entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.CharacterName))
                    .GroupBy(e => e.Job);

                foreach (var jobGroup in byJob)
                {
                    var ranked = jobGroup.OrderByDescending(e => e.Rdps).ToList();
                    for (var i = 0; i < ranked.Count; i++)
                    {
                        var e = ranked[i];
                        allEntries.Add(new RankingEntry
                        {
                            Rank          = i + 1,
                            Boss          = bossName,
                            Category      = category,
                            IsObsolete    = isObsolete,
                            Job           = e.Job,
                            PlayerName    = e.CharacterName,
                            Server        = e.Server,
                            Dps           = e.Dps,
                            Rdps          = e.Rdps,
                            Adps          = e.Adps,
                            FightDuration = e.ClearTimeSeconds,
                        });
                    }
                }
            }

            // ─── 最遠進度（未通關玩家）────────────────────────────────────────
            // 僅零式/絕/滅提供 progress 資料（encounters 帶 progress_path 者）。
            var progressKeys = ParseProgressKeys(encountersJson);
            if (progressKeys.Count > 0)
            {
                var progressFiles = await Task.WhenAll(
                    progressKeys.Select(k => DownloadProgressFile(licenseKey, k)));

                foreach (var file in progressFiles)
                {
                    if (file?.Entries == null || file.Encounter == null) continue;
                    var encKey     = file.Encounter.Key;
                    var bossName   = EncounterMeta.DisplayBossName(encKey, file.Encounter.Name);
                    var category   = file.Encounter.Category;
                    var isObsolete = EncounterMeta.IsObsoleteKey(encKey);

                    foreach (var p in file.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(p.CharacterName)) continue;
                        allEntries.Add(new RankingEntry
                        {
                            Boss          = bossName,
                            Category      = category,
                            IsObsolete    = isObsolete,
                            IsProg        = true,
                            FurthestPhase = EncounterMeta.ProgPhaseLabel(encKey, p.PhaseIndex),
                            PhaseNumber   = p.PhaseIndex,
                            FightPct      = p.FightPercentage,
                            BossPct       = p.BossPercentage,
                            Job           = p.Job,
                            PlayerName    = p.CharacterName,
                            Server        = p.Server,
                        });
                    }
                }

                // 同一玩家同一 boss 若已有清板紀錄，移除其練習條目
                // （避免上游 progress 仍保留已通關者的最遠進度而出現「已過卻顯示練習中」）
                var clearedSet = allEntries
                    .Where(e => !e.IsProg)
                    .Select(e => (Name: e.PlayerName.ToLowerInvariant(), e.Boss))
                    .ToHashSet();
                allEntries.RemoveAll(e => e.IsProg &&
                    clearedSet.Contains((e.PlayerName.ToLowerInvariant(), e.Boss)));
            }

            _index       = BuildIndex(allEntries);
            _lastFetched = DateTime.UtcNow;
            _updated     = DateTime.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            _status      = $"✓ {TotalPlayers} 位玩家 / {TotalEntries} 筆條目（更新：{_updated}）";
            Plugin.Log.Information($"[TCRanking] 載入完成：{_status}");
        }
        catch (Exception ex)
        {
            _status = $"✗ {ex.Message}";
            Plugin.Log.Error(ex, "[TCRanking] 下載/解析失敗");
        }
        finally
        {
            _loading = false;
            _sem.Release();
        }
    }

    // ─── encounters.json 解析（支援多種格式）─────────────────────────────
    private static List<string> ParseEncounterKeys(string json)
    {
        try
        {
            var root = JsonSerializer.Deserialize<KantaiEncountersRoot>(json, JsonOpts);
            if (root?.Encounters != null && root.Encounters.Count > 0)
                return root.Encounters
                    .Where(e => !string.IsNullOrEmpty(e.Key))
                    .Select(e => e.Key)
                    .ToList();
        }
        catch { /* fallback */ }

        try
        {
            var list = JsonSerializer.Deserialize<List<KantaiEncounterInfo>>(json, JsonOpts);
            if (list != null && list.Count > 0)
                return list
                    .Where(e => !string.IsNullOrEmpty(e.Key))
                    .Select(e => e.Key)
                    .ToList();
        }
        catch { /* give up */ }

        Plugin.Log.Warning("[TCRanking] 無法解析 encounters.json 格式");
        return [];
    }

    // ─── 下載單一副本排名檔（失敗時記錄 warning 並回傳 null）───────────────
    private async Task<KantaiRankingFile?> DownloadRankingFile(string licenseKey, string key)
    {
        try
        {
            var json = await GetWithAuthAsync(licenseKey, $"/rankings/{key}");
            return JsonSerializer.Deserialize<KantaiRankingFile>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TCRanking] 無法下載 /rankings/{key}: {ex.Message}");
            return null;
        }
    }

    // ─── encounters.json 取出「有最遠進度資料」的副本 key（progress_path 非空）──
    private static List<string> ParseProgressKeys(string json)
    {
        List<KantaiEncounterInfo>? list = null;
        try { list = JsonSerializer.Deserialize<KantaiEncountersRoot>(json, JsonOpts)?.Encounters; }
        catch { /* fallback */ }
        if (list == null || list.Count == 0)
            try { list = JsonSerializer.Deserialize<List<KantaiEncounterInfo>>(json, JsonOpts); }
            catch { /* give up */ }

        return list?
            .Where(e => !string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.ProgressPath))
            .Select(e => e.Key)
            .ToList() ?? [];
    }

    // ─── 下載單一副本進度檔（失敗時記錄 warning 並回傳 null）───────────────
    private async Task<KantaiProgressFile?> DownloadProgressFile(string licenseKey, string key)
    {
        try
        {
            var json = await GetWithAuthAsync(licenseKey, $"/progress/{key}");
            return JsonSerializer.Deserialize<KantaiProgressFile>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TCRanking] 無法下載 /progress/{key}: {ex.Message}");
            return null;
        }
    }

    // ─── 索引建立 ──────────────────────────────────────────────────────────
    private static Dictionary<string, List<RankingEntry>> BuildIndex(List<RankingEntry> entries)
    {
        var idx = new Dictionary<string, List<RankingEntry>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.PlayerName)) continue;
            var key = e.PlayerName.ToLowerInvariant();
            if (!idx.TryGetValue(key, out var list))
                idx[key] = list = [];
            list.Add(e);
        }
        return idx;
    }

    // ─── 查詢 ──────────────────────────────────────────────────────────────
    // 「名稱＋伺服器」一起比對，避免同名跨服玩家被誤匹配（顯示成別人的成績／進度）。
    public List<RankingEntry> Query(string characterName, string? world = null)
    {
        var key = characterName.ToLowerInvariant();
        if (!_index.TryGetValue(key, out var list)) return [];

        // 過渡期保護：資料尚無 server 欄位（KV 未更新）→ 無從比對，回傳全部
        if (list.All(e => string.IsNullOrEmpty(e.Server))) return list;

        // world 已知（CharaCard 解析）→ 精準比對伺服器
        if (!string.IsNullOrEmpty(world))
            return list
                .Where(e => string.Equals(e.Server, world, StringComparison.OrdinalIgnoreCase))
                .ToList();

        // world 未知（NameCache 成員拿不到伺服器）→ 回傳 best-guess（全部）；
        // 是否同名跨服由 IsCrossServerAmbiguous 另外判斷，UI 以名字旁驚嘆號提示「僅供參考」，
        // 而非隱藏資料。
        return list;
    }

    // 同名是否橫跨多個伺服器：world 未知時用來判斷「無法確定是哪位」，
    // UI 據此顯示驚嘆號（而非當成單純無紀錄）。
    public bool IsCrossServerAmbiguous(string characterName)
    {
        var key = characterName.ToLowerInvariant();
        if (!_index.TryGetValue(key, out var list)) return false;
        return list
            .Select(e => e.Server)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
    }

    // ─── Server sync：CID 快取 ────────────────────────────────────────────────

    public async Task UploadCidCacheAsync(Dictionary<string, string> entries)
    {
        var licenseKey = Plugin.Configuration.GetLicenseKey();
        if (licenseKey == null || entries.Count == 0) return;
        try
        {
            const string path = "/shared/cidcache";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkerBaseUrl}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", BuildHmacHeader(licenseKey, path));
            req.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(entries),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            Plugin.Log.Debug($"[Sync] CID 快取上傳：{resp.StatusCode}，{entries.Count} 筆");
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Sync] CID 快取上傳失敗"); }
    }

    public async Task<Dictionary<string, string>> DownloadSharedCidCacheAsync()
    {
        var licenseKey = Plugin.Configuration.GetLicenseKey();
        if (licenseKey == null) return [];
        try
        {
            var json = await GetWithAuthAsync(licenseKey, "/shared/cidcache");
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Sync] CID 快取下載失敗"); return []; }
    }

    // ─── Server sync：黑名單 ──────────────────────────────────────────────────

    public async Task UploadBlacklistAsync(IEnumerable<BlacklistEntry> entries)
    {
        var licenseKey = Plugin.Configuration.GetLicenseKey();
        if (licenseKey == null) return;
        var list = entries.ToList();
        if (list.Count == 0) return;
        try
        {
            const string path = "/shared/blacklist";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkerBaseUrl}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", BuildHmacHeader(licenseKey, path));
            req.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(list),
                Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            Plugin.Log.Debug($"[Sync] 黑名單上傳：{resp.StatusCode}，{list.Count} 筆");
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Sync] 黑名單上傳失敗"); }
    }

    public async Task<List<BlacklistEntry>> DownloadSharedBlacklistAsync()
    {
        var licenseKey = Plugin.Configuration.GetLicenseKey();
        if (licenseKey == null) return [];
        try
        {
            var json = await GetWithAuthAsync(licenseKey, "/shared/blacklist");
            return System.Text.Json.JsonSerializer.Deserialize<List<BlacklistEntry>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Sync] 黑名單下載失敗"); return []; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _sem.Dispose();
    }
}
