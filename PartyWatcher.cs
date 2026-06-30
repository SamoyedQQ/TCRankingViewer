using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TCRankingViewer;

/// <summary>
/// 每幀比對隊伍成員，偵測新加入的成員並觸發排名查詢 / 通知。
/// 同時維護 CurrentResults 供 MainWindow 顯示。
/// </summary>
public class PartyWatcher : IDisposable
{
    private readonly HashSet<string>         _prevNames    = [];
    private readonly List<PartyMemberResult> _results      = [];
    private readonly object                  _resultsLock  = new();
    private readonly List<string>            _pendingNotify = [];   // 等資料就緒才通知
    private bool _disposed;

    public IReadOnlyList<PartyMemberResult> CurrentResults
    {
        get { lock (_resultsLock) return _results.ToList(); }
    }

    public PartyWatcher()
    {
        Plugin.Framework.Update += OnUpdate;
    }

    // ─── 收集隊伍成員名稱（同伺服器 + 跨伺服器）─────────────────────────────
    // 順手把 (name, world, cid) 餵給 BlacklistService.RecordMeta，
    // 讓黑名單上的玩家在下次上傳時帶上 server 跨服識別資訊
    private static unsafe HashSet<string> CollectPartyNames()
    {
        var names = new HashSet<string>();

        // 同伺服器隊伍
        foreach (var m in Plugin.PartyList)
        {
            var n = m?.Name?.TextValue;
            if (string.IsNullOrEmpty(n)) continue;
            names.Add(n);
            var cid = m!.ContentId != 0 ? (ulong)m.ContentId : 0UL;
            if (cid != 0) Plugin.CidCache.Set(cid, n);
            var worldName = m.World.Value.Name.ToString();
            Plugin.BlacklistService.RecordMeta(n, worldName, cid);
        }

        // 跨伺服器隊伍（InfoProxyCrossRealm）
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy != null && proxy->IsInCrossRealmParty != 0)
        {
            for (var g = 0; g < proxy->GroupCount; g++)
            {
                var group = proxy->CrossRealmGroups[g];
                for (var m = 0; m < group.GroupMemberCount; m++)
                {
                    var member = group.GroupMembers[m];
                    var n = member.NameString;
                    if (string.IsNullOrEmpty(n)) continue;
                    names.Add(n);
                    if (member.ContentId != 0)
                        Plugin.CidCache.Set(member.ContentId, n);
                    // CrossRealmMember 的 HomeWorld 是 short，需 cast 為 uint 再查 ExcelSheet<World>
                    var worldName = ResolveWorldName((uint)member.HomeWorld);
                    Plugin.BlacklistService.RecordMeta(n, worldName, member.ContentId);
                }
            }
        }

        return names;
    }

    // 將 WorldId 轉成 World 名稱；查不到回 null（避免污染 meta）
    // public：PartyFinderInspector 也需用 CharaCard 的 WorldId 解析伺服器名供「名稱＋伺服器」匹配
    public static string? ResolveWorldName(uint worldId)
    {
        if (worldId == 0) return null;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
        return sheet?.GetRowOrDefault(worldId)?.Name.ToString();
    }

    // ─── 每幀更新 ──────────────────────────────────────────────────────────
    private void OnUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (_disposed) return;

        var currentNames = CollectPartyNames();

        var joined = currentNames.Except(_prevNames).ToList();
        var left   = _prevNames.Except(currentNames).ToList();

        _prevNames.Clear();
        foreach (var n in currentNames) _prevNames.Add(n);

        if (joined.Count > 0 || left.Count > 0)
            RebuildResults(currentNames);

        // 對新加入的成員通知
        foreach (var name in joined)
            HandleJoin(name);
    }

    // ─── 重建顯示結果 ──────────────────────────────────────────────────────
    public void RebuildResults() => RebuildResults(CollectPartyNames());

    private void RebuildResults(HashSet<string> currentNames)
    {
        var local     = Plugin.ClientState.LocalPlayer;
        var localName = local?.Name?.TextValue ?? "";

        var fresh = new List<PartyMemberResult>();

        foreach (var name in currentNames)
        {
            var entries = Plugin.RankingService.Query(name);
            fresh.Add(new PartyMemberResult
            {
                CharacterName = name,
                WorldName     = GetWorldName(name),
                IsSelf        = name == localName,
                IsFound       = entries.Count > 0,
                Entries       = entries,
            });
        }

        // 若隊伍為空（Solo），顯示自己
        if (fresh.Count == 0 && local != null)
        {
            var entries = Plugin.RankingService.Query(localName);
            fresh.Add(new PartyMemberResult
            {
                CharacterName = localName,
                WorldName     = local.HomeWorld.Value.Name.ToString(),
                IsSelf        = true,
                IsFound       = entries.Count > 0,
                Entries       = entries,
            });
        }

        lock (_resultsLock)
        {
            _results.Clear();
            _results.AddRange(fresh);
        }

        // 若有等待資料就緒的通知，現在資料已載入就一併發送
        if (Plugin.RankingService.IsReady && _pendingNotify.Count > 0)
        {
            var toNotify = _pendingNotify.ToList();
            _pendingNotify.Clear();
            foreach (var n in toNotify) Notify(n);
            if (Plugin.Configuration.AutoOpenWindow)
                Plugin.MainWindow.IsOpen = true;
        }
    }

    // ─── 處理新加入 ────────────────────────────────────────────────────────
    private void HandleJoin(string name)
    {
        if (!Plugin.RankingService.IsReady)
        {
            // 資料尚未就緒：存入待通知清單，等 RebuildResults 再發
            _pendingNotify.Add(name);
            return;
        }
        Notify(name);

        if (Plugin.Configuration.AutoOpenWindow)
            Plugin.MainWindow.IsOpen = true;
    }

    private static void Notify(string name)
    {
        if (!Plugin.Configuration.ChatNotifyOnJoin) return;

        var entries = Plugin.RankingService.Query(name);
        var cleared = entries.Where(e => !e.IsProg).ToList();
        if (cleared.Count > 0)
        {
            var best = cleared.MinBy(e => e.Rank)!;
            var msg  = $"[TC排名] {name} 加入隊伍 ─ " +
                       $"{best.Boss} #{best.Rank} " +
                       $"{JobAbbrev.Get(best.Job)} rDPS {best.Rdps:F0}";
            Plugin.ChatGui.Print(new SeString(new TextPayload(msg)));
        }
        else if (Plugin.Configuration.NotifyUnranked)
        {
            Plugin.ChatGui.Print(new SeString(
                new TextPayload($"[TC排名] {name} 加入隊伍 ─ 未在排名中找到")));
        }
    }

    // ─── 工具 ──────────────────────────────────────────────────────────────
    private static string GetWorldName(string characterName)
    {
        // TC 伺服器是 Hades / Typhon / Carbuncle 等，嘗試從 PartyList 取得
        foreach (var m in Plugin.PartyList)
        {
            if (m?.Name?.TextValue == characterName)
                return m.World.Value.Name.ToString();
        }
        return "";
    }

    public void Dispose()
    {
        _disposed = true;
        Plugin.Framework.Update -= OnUpdate;
    }
}
