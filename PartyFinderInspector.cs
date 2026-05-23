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

        SlotsFilled          = slotsFilled;
        TotalSlots           = totalSlots;
        CurrentRecruiterName = slots.Count > 0 && slots[0].name != null ? slots[0].name! : "";
        var leaderCid = slots.Count > 0 ? slots[0].cid : 0UL;
        CurrentEncounterCat  = _encounterCatCache.TryGetValue(leaderCid, out var ec) ? ec : "";
        CurrentDutyName      = _dutyNameCache.TryGetValue(leaderCid, out var dn) ? dn : "";

        // Build _staged in slot order; unknown CIDs get empty placeholders replaced by CharaCard results
        var fresh        = new List<PartyMemberResult>(slots.Count);
        var unknownSlots = new List<(int idx, ulong cid, string job)>();

        for (var i = 0; i < slots.Count; i++)
        {
            var (name, job, cid) = slots[i];
            if (name != null)
            {
                var entries = Plugin.RankingService.Query(name);
                fresh.Add(new PartyMemberResult
                {
                    CharacterName = name,
                    CurrentJob    = job,
                    IsFound       = entries.Count > 0,
                    Entries       = entries,
                });
            }
            else if (cid == 0)
            {
                fresh.Add(new PartyMemberResult { IsUnresolvable = true });
            }
            else
            {
                fresh.Add(new PartyMemberResult()); // placeholder — replaced by CharaCard
                unknownSlots.Add((i, cid, job));
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
            Interlocked.Exchange(ref _pendingLookups, unknownSlots.Count);
            var cts = new CancellationTokenSource();
            _lookupCts = cts;
            _ = LookupUnknownNamesAsync(unknownSlots, cts.Token);
        }
    }

    // ── CharaCard async lookup — updates _staged at the correct slot index ───────
    private async Task LookupUnknownNamesAsync(
        List<(int idx, ulong cid, string job)> unknownSlots, CancellationToken ct)
    {
        foreach (var (idx, cid, preJob) in unknownSlots)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await Plugin.CharaCardLookup.RequestAsync(cid, ct);
                lock (_lock)
                {
                    PartyMemberResult pmr;
                    if (!string.IsNullOrEmpty(result.Name))
                    {
                        var entries = Plugin.RankingService.Query(result.Name);
                        // Prefer slot-cached job; fall back to CharaCard ClassJobId
                        var job = !string.IsNullOrEmpty(preJob) ? preJob
                            : result.ClassJobId != 0 ? JobAbbrev.GetByJobId(result.ClassJobId)
                            : _jobCache.TryGetValue(cid, out var cj) ? cj : "";
                        pmr = new PartyMemberResult
                        {
                            CharacterName = result.Name,
                            CurrentJob    = job,
                            IsFound       = entries.Count > 0,
                            Entries       = entries,
                        };
                        Plugin.CidCache.Set(cid, result.Name);
                        Plugin.Log.Debug($"[PFInspector] CharaCard 解析 {cid:X16} → {result.Name}");
                    }
                    else
                    {
                        pmr = new PartyMemberResult { IsUnresolvable = true };
                        Plugin.Log.Debug($"[PFInspector] CharaCard 無法解析 {cid:X16}，加入佔位列");
                    }

                    if (idx < _staged.Count)
                        _staged[idx] = pmr;

                    UnresolvedCount = Math.Max(0, UnresolvedCount - 1);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex,
                    $"[PFInspector] CharaCard 查詢失敗 {cid:X16}");
            }
            finally
            {
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

        var detailed  = &agent->LastViewedListing;
        var filled    = (int)detailed->SlotsFilled;
        var total     = (int)detailed->TotalSlots;
        var leaderCid = detailed->LeaderContentId;

        // Jobs in slot order (index 0 = leader, 1+ = members) from latest ReceiveListing
        string[] cachedJobs = _rawJobsCache.TryGetValue(leaderCid, out var rj) ? rj : [];

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

            if (slots.Count >= filled) break;
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
