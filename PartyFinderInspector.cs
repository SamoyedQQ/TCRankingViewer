using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TCRankingViewer;

/// <summary>
/// Detects when the LookingForGroupDetail popup opens, reads all member names
/// (or ContentIds), resolves names via NameCache / ReceiveListing cache / CharaCard,
/// and queries rankings for the PartyFinderWindow.
/// </summary>
public sealed class PartyFinderInspector : IDisposable
{
    // ReceiveListing caches
    private readonly Dictionary<ulong, string>   _contentIdCache       = new();
    private readonly Dictionary<ulong, string>   _jobCache             = new();
    private readonly Dictionary<ulong, string[]> _rawJobsCache         = new();
    private readonly Dictionary<ulong, string>   _encounterCatCache    = new();
    private readonly Dictionary<ulong, string>   _dutyNameCache        = new();

    private readonly List<PartyMemberResult> _results = [];
    private readonly List<PartyMemberResult> _staged  = [];
    private readonly object _lock = new();
    private bool _disposed;

    // Async lookup state
    private CancellationTokenSource? _lookupCts;
    private int _pendingLookups;

    public bool   IsOpen                  { get; private set; }
    public string CurrentRecruiterName   { get; private set; } = "";
    public string CurrentEncounterCat    { get; private set; } = "";
    public string CurrentDutyName        { get; private set; } = "";
    public int    SlotsFilled            { get; private set; }
    public int    TotalSlots             { get; private set; }
    public int    UnresolvedCount        { get; private set; }
    public int    PendingLookups         => _pendingLookups;

    public IReadOnlyList<PartyMemberResult> Results
    {
        get { lock (_lock) return _results.ToList(); }
    }

    public PartyFinderInspector()
    {
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,   "LookingForGroupDetail", OnDetailOpen);
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "LookingForGroupDetail", OnDetailOpen);
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "LookingForGroupDetail", OnDetailClose);

        Plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;
    }

    // ── ReceiveListing: cache leader name + all current slot jobs ───────────────
    private void OnReceiveListing(
        IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        var name = listing.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name) || listing.ContentId == 0) return;

        _contentIdCache[listing.ContentId] = name;
        Plugin.CidCache.Set(listing.ContentId, name);

        var cat = DetectEncounterCategory(listing.RawDuty);
        if (!string.IsNullOrEmpty(cat))
            _encounterCatCache[listing.ContentId] = cat;

        if (listing.RawDuty != 0)
        {
            try
            {
                var cfcRow = Plugin.DataManager
                    .GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>()
                    .GetRow(listing.RawDuty);
                var dn = cfcRow.Name.ToString();
                if (!string.IsNullOrEmpty(dn))
                    _dutyNameCache[listing.ContentId] = dn;
            }
            catch { }
        }

        // Cache full slot-order job abbreviations keyed by leader CID
        var rawJobs = listing.RawJobsPresent;
        if (rawJobs.Count > 0)
        {
            _rawJobsCache[listing.ContentId] = rawJobs
                .Select(j => JobAbbrev.GetByJobId(j))
                .ToArray();
        }

        // Individual CID fallback for the leader
        var firstJob = rawJobs.FirstOrDefault();
        if (firstJob != 0)
        {
            var abbrev = JobAbbrev.GetByJobId(firstJob);
            if (!string.IsNullOrEmpty(abbrev))
                _jobCache[listing.ContentId] = abbrev;
        }
    }

    // ── Addon opened ────────────────────────────────────────────────────────────
    private void OnDetailOpen(AddonEvent ev, AddonArgs args)
    {
        if (_disposed) return;
        IsOpen = true;
        RefreshFromAgent(args.Addon);
        Plugin.Framework.RunOnTick(() =>
        {
            if (!_disposed)
            {
                Plugin.PartyFinderWindow.IsOpen = true;
                if (!string.IsNullOrEmpty(CurrentEncounterCat))
                    Plugin.PartyFinderWindow.SetInitialCategoryFilter(CurrentEncounterCat);
            }
        });
    }

    // ── Addon closed ────────────────────────────────────────────────────────────
    private void OnDetailClose(AddonEvent ev, AddonArgs args)
    {
        if (_disposed) return;
        IsOpen = false;
        CancelPendingLookups();
        CurrentRecruiterName = "";
        CurrentEncounterCat  = "";
        CurrentDutyName      = "";
        SlotsFilled          = 0;
        TotalSlots           = 0;
        UnresolvedCount      = 0;
        lock (_lock) { _results.Clear(); _staged.Clear(); }
        Plugin.Framework.RunOnTick(() =>
        {
            if (!_disposed) Plugin.PartyFinderWindow.IsOpen = false;
        });
    }

    // ── Rebuild results from AgentLookingForGroup ────────────────────────────────
    public void RefreshFromAgent(nint addonPtr)
    {
        CancelPendingLookups();

        var (slots, slotsFilled, totalSlots) = CollectMemberSlots(addonPtr);

        // 防禦性：若收集到的成員數超過遊戲回報，將多餘條目視為遊戲記憶體殘留並丟棄
        // （正常情況下 CollectMemberSlots 已透過 break 條件防止這種情況）
        if (slots.Count > slotsFilled)
        {
            Plugin.Log.Debug(
                $"[PFInspector] 收集 {slots.Count} 槽位但 SlotsFilled={slotsFilled}，丟棄末端多餘條目");
            slots.RemoveRange(slotsFilled, slots.Count - slotsFilled);
        }

        SlotsFilled          = slotsFilled;
        TotalSlots           = totalSlots;
        CurrentRecruiterName = slots.Count > 0 && slots[0].name != null ? slots[0].name! : "";
        var leaderCid = slots.Count > 0 ? slots[0].cid : 0UL;
        CurrentEncounterCat  = _encounterCatCache.TryGetValue(leaderCid, out var ec) ? ec : "";
        CurrentDutyName      = _dutyNameCache.TryGetValue(leaderCid, out var dn) ? dn : "";

        // Build _staged in slot order; unknown CIDs get empty placeholders replaced by CharaCard results
        var fresh        = new List<PartyMemberResult>(slots.Count);
        // knownName != null：已具名但同名跨服 → 需 CharaCard 補 world 後精準比對
        var unknownSlots = new List<(int idx, ulong cid, string job, string? knownName)>();

        for (var i = 0; i < slots.Count; i++)
        {
            var (name, job, cid) = slots[i];
            if (name != null)
            {
                // NameCache 解析的成員拿不到 world（遊戲 ContentId 快取只有名字、無伺服器）
                var entries   = Plugin.RankingService.Query(name);   // best-guess
                var ambiguous = Plugin.RankingService.IsCrossServerAmbiguous(name);
                if (ambiguous && cid != 0)
                {
                    // 同名跨服 → 只對這少數人排程 CharaCard 補 world 後精準比對（不全員開卡）。
                    // 先顯示 best-guess 資料 + 名字旁 ！；CharaCard 成功會覆寫為精準結果並移除 ！
                    unknownSlots.Add((i, cid, job, name));
                }
                fresh.Add(new PartyMemberResult
                {
                    CharacterName        = name,
                    CurrentJob           = job,
                    IsFound              = entries.Count > 0,
                    Entries              = entries,
                    AmbiguousCrossServer = ambiguous,
                });
            }
            else if (cid == 0)
            {
                fresh.Add(new PartyMemberResult { IsUnresolvable = true });
            }
            else
            {
                fresh.Add(new PartyMemberResult()); // placeholder — replaced by CharaCard
                unknownSlots.Add((i, cid, job, null));
            }
        }

        UnresolvedCount = unknownSlots.Count;

        lock (_lock)
        {
            _staged.Clear();
            _staged.AddRange(fresh);
            _results.Clear();
            if (unknownSlots.Count == 0)
                _results.AddRange(_staged);
            // else: keep _results empty until all CharaCard lookups finish
        }

        Plugin.Log.Debug(
            $"[PFInspector] 隊伍 {slotsFilled}/{totalSlots} " +
            $"已解析 {slots.Count - unknownSlots.Count} 人，未解析 {unknownSlots.Count} 人");

        if (unknownSlots.Count > 0)
        {
            if (Plugin.Configuration.AutoResolveViaCharaCard)
            {
                Interlocked.Exchange(ref _pendingLookups, unknownSlots.Count);
                var cts = new CancellationTokenSource();
                _lookupCts = cts;
                _ = LookupUnknownNamesAsync(unknownSlots, cts.Token);
            }
            else
            {
                // 使用者關閉自動開卡 → 不觸發任何 CharaCard（零閃現）。
                // 純未知名（knownName == null）的佔位列標為「無法解析」；同名跨服者維持
                // best-guess 資料（已在 _staged 內），直接發布結果。
                lock (_lock)
                {
                    foreach (var (idx, _, _, knownName) in unknownSlots)
                        if (knownName == null && idx < _staged.Count)
                            _staged[idx] = new PartyMemberResult { IsUnresolvable = true };
                    _results.Clear();
                    _results.AddRange(_staged);
                }
                UnresolvedCount = unknownSlots.Count(s => s.knownName == null);
            }
        }
    }

    // ── CharaCard async lookup — updates _staged at the correct slot index ───────
    // 注意：此方法被取消時必須跳過 _pendingLookups 的遞減，否則會污染新批次的計數
    // 並導致新批次 _staged 內未處理完的 placeholder 被誤發布為「空名字 + 無紀錄」列
    private async Task LookupUnknownNamesAsync(
        List<(int idx, ulong cid, string job, string? knownName)> unknownSlots, CancellationToken ct)
    {
        foreach (var (idx, cid, preJob, knownName) in unknownSlots)
        {
            if (ct.IsCancellationRequested) return;

            PartyMemberResult? pmr = null;
            try
            {
                var result = await Plugin.CharaCardLookup.RequestAsync(cid, ct);
                if (ct.IsCancellationRequested) return; // 取消後丟棄結果，避免覆寫新批次的 _staged

                if (!string.IsNullOrEmpty(result.Name))
                {
                    // CharaCard 帶回 WorldId → 解析伺服器名，查詢時一起比對避免同名跨服誤匹配
                    var world   = PartyWatcher.ResolveWorldName(result.WorldId) ?? "";
                    var entries = Plugin.RankingService.Query(result.Name, world);
                    // Prefer slot-cached job; fall back to CharaCard ClassJobId
                    var job = !string.IsNullOrEmpty(preJob) ? preJob
                        : result.ClassJobId != 0 ? JobAbbrev.GetByJobId(result.ClassJobId)
                        : _jobCache.TryGetValue(cid, out var cj) ? cj : "";
                    pmr = new PartyMemberResult
                    {
                        CharacterName = result.Name,
                        WorldName     = world,
                        CurrentJob    = job,
                        IsFound       = entries.Count > 0,
                        Entries       = entries,
                    };
                    Plugin.CidCache.Set(cid, result.Name);
                    Plugin.Log.Debug($"[PFInspector] CharaCard 解析 {cid:X16} → {result.Name}");
                }
                else
                {
                    // 已具名（同名跨服）但 CharaCard 補 world 失敗 → 保留名稱 + 驚嘆號提示，
                    // 而非當成完全無法解析；純未知名才標 IsUnresolvable
                    pmr = !string.IsNullOrEmpty(knownName)
                        ? new PartyMemberResult { CharacterName = knownName, CurrentJob = preJob, AmbiguousCrossServer = true }
                        : new PartyMemberResult { IsUnresolvable = true };
                    Plugin.Log.Debug($"[PFInspector] CharaCard 無法解析 {cid:X16}，加入佔位列");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex,
                    $"[PFInspector] CharaCard 查詢失敗 {cid:X16}");
                // 查詢失敗也要寫回 _staged，避免遺留空 placeholder 被誤發布為「空名字 + 無紀錄」列
                pmr = !string.IsNullOrEmpty(knownName)
                    ? AmbiguousFallback(knownName!, preJob)
                    : new PartyMemberResult { IsUnresolvable = true };
            }

            // 寫回 _staged 與遞減計數都要在「未被取消」時才執行，否則會影響新批次
            if (ct.IsCancellationRequested) return;

            lock (_lock)
            {
                if (pmr != null && idx < _staged.Count)
                    _staged[idx] = pmr;
                UnresolvedCount = Math.Max(0, UnresolvedCount - 1);
            }

            var remaining = Interlocked.Decrement(ref _pendingLookups);
            if (remaining == 0)
            {
                lock (_lock)
                {
                    _results.Clear();
                    _results.AddRange(_staged);
                }
                Plugin.Log.Debug("[PFInspector] 所有查詢完成，發布結果");
            }
        }
    }

    // 同名跨服但 CharaCard 補 world 失敗 → 保留名稱 + best-guess 資料 + ！ 提示
    private static PartyMemberResult AmbiguousFallback(string knownName, string job)
    {
        var entries = Plugin.RankingService.Query(knownName);
        return new PartyMemberResult
        {
            CharacterName        = knownName,
            CurrentJob           = job,
            Entries              = entries,
            IsFound              = entries.Count > 0,
            AmbiguousCrossServer = true,
        };
    }

    private void CancelPendingLookups()
    {
        Interlocked.Exchange(ref _pendingLookups, 0);
        var old = _lookupCts;
        _lookupCts = null;
        old?.Cancel();
        old?.Dispose();
    }

    // ── Collect all member slots in visual slot order ────────────────────────────
    // Returns (name, job, cid) tuples where name==null means unresolved (needs CharaCard).
    private unsafe (List<(string? name, string job, ulong cid)> slots,
                    int filled, int total)
        CollectMemberSlots(nint addonPtr)
    {
        var slots = new List<(string? name, string job, ulong cid)>();

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            var fallback = FallbackFromTextNode(addonPtr);
            if (fallback != null) slots.Add((fallback, "", 0UL));
            return (slots, slots.Count, slots.Count);
        }

        var detailed   = &agent->LastViewedListing;
        var filled     = (int)detailed->SlotsFilled;
        var total      = (int)detailed->TotalSlots;
        var leaderCid  = detailed->LeaderContentId;
        var isAlliance = total > 8;   // 24 人聯盟團（A／B／C 三隊）只有此情況 total > 8

        // Jobs in slot order (index 0 = leader, 1+ = members)。
        // 一般 8 人本沿用 Dalamud ReceiveListing 的 RawJobsPresent（含跨服快取）。
        // 聯盟團特別處理：Dalamud RawJobsPresent 只涵蓋 8 格，對 24 人本會少報，改用 agent
        // Detailed 的並列陣列（MemberContentIds[i] ↔ Jobs[i]）建立 cid→職業對照，交由
        // GetJobAtSlot 以 cid fallback 取職業，避免與 RawJobsPresent 的視覺槽位索引混淆。
        // 先以 agent Detailed 的並列陣列（MemberContentIds[i] ↔ Jobs[i]）建立 cid→職業快取。
        // 原本只有聯盟團會做這步；但一般 8 人本若只靠 Dalamud RawJobsPresent，會在該快取尚未
        // 就緒時（剛開招募面板、清單還沒觸發 ReceiveListing）整批取不到職業，導致「現職」icon
        // 全部消失。改為一律先補 _jobCache 作為後備，RawJobsPresent 未就緒時由 GetJobAtSlot 回退。
        var ids  = detailed->MemberContentIds;
        var jobs = detailed->Jobs;
        for (var i = 0; i < ids.Length && i < jobs.Length; i++)
        {
            var cid = ids[i];
            if (cid == 0) continue;
            var ab = JobAbbrev.GetByJobId(jobs[i]);
            if (!string.IsNullOrEmpty(ab)) _jobCache[cid] = ab;
        }

        // 聯盟團（24 人）的 RawJobsPresent 只涵蓋 8 格會少報，直接靠上面的 _jobCache（Detailed）；
        // 一般 8 人本優先用 RawJobsPresent（含跨服快取、較準），未就緒時再回退 _jobCache。
        var cachedJobs = isAlliance
            ? []
            : (_rawJobsCache.TryGetValue(leaderCid, out var rj) ? rj : []);

        // 校正 filled（僅一般 8 人本）：SlotsFilled 偶爾被遊戲記憶體殘留污染（成員離開但
        // count 沒更新），此時 RawJobsPresent 較準。聯盟團不套用此校正——RawJobsPresent
        // 只有 8 格，會把實際 17 人誤砍成 8（即「人數鎖在 8 人」），故直接信任 SlotsFilled。
        if (!isAlliance && cachedJobs.Length > 0 && cachedJobs.Length < filled)
        {
            Plugin.Log.Debug(
                $"[PFInspector] SlotsFilled={filled} 但 RawJobsPresent 只有 {cachedJobs.Length} 人，" +
                $"以 {cachedJobs.Length} 為實際成員數");
            filled = cachedJobs.Length;
        }

        // ── 1. Leader (slot 0) ───────────────────────────────────────────────────
        var leaderName = detailed->LeaderString;
        if (string.IsNullOrEmpty(leaderName))
            leaderName = ReadLeaderFromAddon(addonPtr);

        slots.Add((
            string.IsNullOrEmpty(leaderName) ? null : leaderName,
            GetJobAtSlot(0, leaderCid, cachedJobs),
            leaderCid
        ));

        // ── 2. Other members via NameCache / ReceiveListing ──────────────────────
        var nameCache = NameCache.Instance();
        var memberIds = detailed->MemberContentIds;

        // Track how many times the leader's CID appeared in MemberContentIds.
        // When the game includes the leader in this array (at index 0), subsequent members
        // would get jobSlotIdx = i+1 which is one too high; subtract the count to correct it.
        var leaderOccurrences = 0;
        for (var i = 0; i < memberIds.Length; i++)
        {
            // 先判斷是否已收集足夠成員，避免多讀遊戲殘留的非零 CID
            if (slots.Count >= filled) break;

            var cid = memberIds[i];
            if (cid == 0) continue;
            if (cid == leaderCid) { leaderOccurrences++; continue; }

            // MemberContentIds[i] corresponds to visual slot i+1 (slot 0 = leader).
            // If the leader's CID also appears in this array, it doesn't consume an extra
            // visual slot, so subtract leaderOccurrences to keep the slot index correct.
            var jobSlotIdx = i + 1 - leaderOccurrences;
            var memberName = ResolveName(cid, nameCache);

            if (!string.IsNullOrEmpty(memberName))
            {
                // skip duplicates (shouldn't happen in practice)
                if (!slots.Any(s => s.name == memberName))
                    slots.Add((memberName, GetJobAtSlot(jobSlotIdx, cid, cachedJobs), cid));
            }
            else
            {
                slots.Add((null, GetJobAtSlot(jobSlotIdx, cid, cachedJobs), cid));
                Plugin.Log.Debug(
                    $"[PFInspector] ContentId {cid:X16} 未快取，排程 CharaCard 查詢");
            }
        }

        return (slots, filled, total);
    }

    // ── Get job abbreviation for a slot, preferring slot-indexed cache ───────────
    private string GetJobAtSlot(int slotIdx, ulong cid, string[] cachedJobs)
    {
        if (slotIdx < cachedJobs.Length && !string.IsNullOrEmpty(cachedJobs[slotIdx]))
            return cachedJobs[slotIdx];
        return _jobCache.TryGetValue(cid, out var j) ? j : "";
    }

    // ── Resolve ContentId → name ─────────────────────────────────────────────────
    private unsafe string? ResolveName(ulong contentId, NameCache* nameCache)
    {
        if (contentId == 0) return null;

        if (nameCache != null)
        {
            var ptr = nameCache->GetNameByContentId(contentId);
            if (ptr.HasValue)
            {
                var name = ptr.ToString();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }

        if (_contentIdCache.TryGetValue(contentId, out var cached)) return cached;

        if (Plugin.CidCache.TryGet(contentId, out var persisted) &&
            !string.IsNullOrEmpty(persisted))
        {
            Plugin.Log.Debug($"[PFInspector] CID {contentId:X16} 從持久快取解析 → {persisted}");
            return persisted;
        }

        return null;
    }

    // ── Read leader name from addon TextNode (fallback) ──────────────────────────
    private static unsafe string? ReadLeaderFromAddon(nint addonPtr)
    {
        if (addonPtr == 0) return null;
        var addon = (AddonLookingForGroupDetail*)addonPtr;
        if (addon == null || addon->PartyLeaderTextNode == null) return null;
        var n = addon->PartyLeaderTextNode->NodeText.ToString();
        return string.IsNullOrEmpty(n) ? null : n;
    }

    // ── Last-resort: scan text nodes for a name in the ranking DB ────────────────
    private static unsafe string? FallbackFromTextNode(nint addonPtr)
    {
        if (addonPtr == 0 || !Plugin.RankingService.IsReady) return null;
        var addon =
            (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr;
        for (var i = 0u; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null ||
                node->Type != FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
                continue;
            var text =
                ((FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)node)
                ->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text) &&
                Plugin.RankingService.Query(text).Count > 0)
                return text;
        }
        return null;
    }

    // ContentFinderCondition ContentType.RowId: 28=絕(Ultimate), 5=零式(Raid), 4/30=極幻(Trial/Unreal)
    private static string DetectEncounterCategory(ushort rawDuty)
    {
        if (rawDuty == 0) return "";
        try
        {
            var row = Plugin.DataManager
                .GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>()
                .GetRow(rawDuty);
            // 滅 Chaotic 聯盟團優先以副本名判定：其 ContentType 可能與一般 Raid 重疊，
            // 名稱比對（黑暗之雲 / 暗之雲）較可靠，故置於 ContentType switch 之前
            if (EncounterMeta.IsChaoticDutyName(row.Name.ToString())) return "滅";
            return row.ContentType.RowId switch
            {
                28     => "絕",
                5      => "零式",
                4 or 30 => "極/幻",
                _      => ""
            };
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPendingLookups();
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PostSetup,   "LookingForGroupDetail", OnDetailOpen);
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PostRefresh, "LookingForGroupDetail", OnDetailOpen);
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PreFinalize, "LookingForGroupDetail", OnDetailClose);
        Plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
    }
}
