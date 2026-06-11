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
                var bossName   = EncounterMeta.NormalizeBossName(file.Encounter.Name);
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
                            Dps           = e.Dps,
                            Rdps          = e.Rdps,
                            Adps          = e.Adps,
                            FightDuration = e.ClearTimeSeconds,
                        });
                    }
                }
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
    public List<RankingEntry> Query(string characterName)
    {
        var key = characterName.ToLowerInvariant();
        return _index.TryGetValue(key, out var list) ? list : [];
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
