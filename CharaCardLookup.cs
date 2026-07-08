using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TCRankingViewer;

public sealed class CharaCardLookup : IDisposable
{
    private const string PacketSig =
        "40 55 53 57 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC B8 04 00 00";
    private const int ThrottleMs     = 200;
    private const int CleanupGraceMs = 800;   // keep monitoring/suppressing this long after request ends
    private const int AddonTailMs    = 500;   // keep monitoring/suppressing this long after each addon sighting
    private const int HardCutoffMs   = 8000;  // absolute ceiling — must never stay muted longer than this

    private delegate void PacketDelegate(nint self, nint packet, byte a3);

    public readonly record struct CharaCardResult(string? Name, byte ClassJobId, ushort WorldId);

    private readonly Hook<PacketDelegate> _hook;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TaskCompletionSource<CharaCardResult>? _tcs;
    private long _pendingCid;    // != 0 while a RequestAsync is in flight
    private long _cleanupUntil;  // continue monitoring/suppressing until this tick
    private long _hardCutoff;    // absolute deadline to force-unmute (safety net)
    private bool _soundMuted;
    private uint _savedSoundSystem; // saved 系統音 volume
    private uint _savedSoundSe;     // saved 音效 (SE) volume

    public CharaCardLookup()
    {
        _hook = Plugin.GameInteropProvider.HookFromSignature<PacketDelegate>(PacketSig, Detour);
        _hook.Enable();
        Plugin.Framework.Update    += OnFrameworkUpdate;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.ToastGui.Toast      += OnToast;
        Plugin.ToastGui.ErrorToast += OnErrorToast;
        // 「繪製前隱藏」：在 CharaCard addon 建立/更新/繪製的最早時機就隱藏，
        // 比等待下一個 Framework.Update 更早，顯著降低偶爾閃現一幀的機率。
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreSetup,   "CharaCard", OnCharaCardEarly);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRefresh, "CharaCard", OnCharaCardEarly);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "CharaCard", OnCharaCardEarly);
    }

    // 我方查詢作用中時，一偵測到 CharaCard 就隱藏（繪製前）。非作用中則完全不干預，
    // 讓使用者正常手動開卡不受影響。
    private unsafe void OnCharaCardEarly(AddonEvent ev, AddonArgs args)
    {
        if (!IsActive(Environment.TickCount64)) return;
        var agent = AgentCharaCard.Instance();
        if (agent != null && agent->IsAddonShown())
        {
            agent->HideAddon();
            ExtendCleanup(ref _cleanupUntil, Environment.TickCount64 + AddonTailMs);
        }
    }

    // Active = a request is in flight OR we're inside the cleanup grace window.
    // This is the single source of truth for "should we be hiding the addon /
    // suppressing chat-toasts / staying muted". Replaces the old _suppressDeadline,
    // which conflated mute-timing with addon-monitoring and broke under game lag.
    private bool IsActive(long now)
        => Interlocked.Read(ref _pendingCid) != 0
        || Interlocked.Read(ref _cleanupUntil) > now;

    // ── Message / toast suppression ───────────────────────────────────────────
    private void OnChatMessage(
        XivChatType type, int timestamp,
        ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled) return;
        if (!IsActive(Environment.TickCount64)) return;
        if (message.TextValue.Contains("冒險者銘牌"))
        {
            isHandled = true;
            Plugin.Log.Debug("[CharaCardLookup] suppressed chat msg");
        }
    }

    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if (isHandled) return;
        if (!IsActive(Environment.TickCount64)) return;
        if (message.TextValue.Contains("冒險者銘牌"))
        {
            isHandled = true;
            Plugin.Log.Debug("[CharaCardLookup] suppressed toast");
        }
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (isHandled) return;
        if (!IsActive(Environment.TickCount64)) return;
        if (message.TextValue.Contains("冒險者銘牌"))
        {
            isHandled = true;
            Plugin.Log.Debug("[CharaCardLookup] hidden plate error — resolving immediately");
            // Player's adventure plate is private; resolve with empty rather than waiting 5s.
            // RequestAsync's finally will arm the cleanup window after tcs resolves,
            // and OnFrameworkUpdate will unmute once that window expires.
            Volatile.Read(ref _tcs)?.TrySetResult(default);
        }
    }

    // ── 系統音 volume muting (framework thread only) ─────────────────────────
    private void TryMuteSound()
    {
        if (_soundMuted) return;
        try
        {
            bool muted = false;
            Plugin.GameConfig.System.TryGet("SoundSystem", out _savedSoundSystem);
            Plugin.GameConfig.System.TryGet("SoundSe",     out _savedSoundSe);
            Plugin.Log.Debug(
                $"[CharaCardLookup] TryMute: SoundSystem={_savedSoundSystem} SoundSe={_savedSoundSe}");
            if (_savedSoundSystem > 0) { Plugin.GameConfig.System.Set("SoundSystem", 0u); muted = true; }
            if (_savedSoundSe     > 0) { Plugin.GameConfig.System.Set("SoundSe",     0u); muted = true; }
            if (muted)
            {
                _soundMuted = true;
                Plugin.Log.Debug("[CharaCardLookup] muted 系統音+音效");
            }
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "[CharaCardLookup] mute failed"); }
    }

    private void TryUnmuteSound()
    {
        if (!_soundMuted) return;
        _soundMuted = false;
        try
        {
            if (_savedSoundSystem > 0) Plugin.GameConfig.System.Set("SoundSystem", _savedSoundSystem);
            if (_savedSoundSe     > 0) Plugin.GameConfig.System.Set("SoundSe",     _savedSoundSe);
            Plugin.Log.Debug(
                $"[CharaCardLookup] restored sys={_savedSoundSystem} se={_savedSoundSe}");
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "[CharaCardLookup] unmute failed"); }
    }

    // Extend _cleanupUntil to at least `target` — never shorten it.
    private static void ExtendCleanup(ref long field, long target)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref field);
            if (target <= current) return;
        } while (Interlocked.CompareExchange(ref field, target, current) != current);
    }

    // ── Every frame: hide the CharaCard addon, govern unmute timing ──────────
    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        var hardCutoff = Interlocked.Read(ref _hardCutoff);
        var hardCutoffHit = hardCutoff != 0 && now > hardCutoff;
        var active = IsActive(now);

        // Hide addon whenever we're still expecting/handling one.
        // Also try once if the hard cutoff was hit, in case an addon is stuck open.
        if (active || hardCutoffHit)
        {
            var agent = AgentCharaCard.Instance();
            if (agent != null && agent->IsAddonShown())
            {
                Plugin.Log.Debug("[CharaCardLookup] CharaCard addon shown — HideAddon()");
                agent->HideAddon();
                // Extend cleanup window so the close-animation sound is suppressed,
                // and so any late re-opens within the tail get caught too.
                ExtendCleanup(ref _cleanupUntil, now + AddonTailMs);
            }
        }

        // Unmute decision — single place that ever clears muting.
        if (!_soundMuted) return;

        if (hardCutoffHit)
        {
            // Something went catastrophically wrong (game stuck, packet never arrived,
            // addon never appeared). Force unmute to avoid permanent silence.
            Plugin.Log.Warning("[CharaCardLookup] hard cutoff reached — forcing unmute");
            TryUnmuteSound();
            Interlocked.Exchange(ref _hardCutoff,   0);
            Interlocked.Exchange(ref _cleanupUntil, 0);
        }
        else if (!active)
        {
            Plugin.Log.Debug("[CharaCardLookup] cleanup window ended — unmute");
            TryUnmuteSound();
            Interlocked.Exchange(ref _hardCutoff, 0);
        }
    }

    // ── Detour: fires on framework thread when the packet arrives ─────────────
    private unsafe void Detour(nint selfPtr, nint packet, byte a3)
    {
        _hook.Original(selfPtr, packet, a3);
        try
        {
            var self = (AgentCharaCard*)selfPtr;
            Plugin.Log.Debug(
                $"[CharaCardLookup] Detour fired, self={selfPtr:X}, " +
                $"Data={(nint)self->Data:X}");

            if (self == null || self->Data == null) return;

            var cid  = self->Data->ContentId;
            var mine = (ulong)Interlocked.Read(ref _pendingCid);
            Plugin.Log.Debug($"[CharaCardLookup] packet cid={cid:X16} pending={mine:X16}");

            if (cid == 0 || cid != mine) return; // not our packet

            var name   = self->Data->Name.ToString();
            var jobId  = self->Data->ClassJobId;
            var worldId = self->Data->WorldId;
            Plugin.Log.Debug($"[CharaCardLookup] match! name={name} jobId={jobId} world={worldId}");

            Volatile.Read(ref _tcs)?.TrySetResult(new CharaCardResult(
                string.IsNullOrEmpty(name) ? null : name, jobId, worldId));
            // No deadline arithmetic here. _pendingCid is still set (RequestAsync's
            // finally hasn't run yet), so OnFrameworkUpdate keeps hiding the addon.
            // Once the finally runs, _cleanupUntil takes over for the tail.
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[CharaCardLookup] Detour error");
        }
    }

    private unsafe void SendOpenRequest(
        ulong contentId, TaskCompletionSource<CharaCardResult> tcs)
    {
        var agent = AgentCharaCard.Instance();
        Plugin.Log.Debug(
            $"[CharaCardLookup] SendOpenRequest cid={contentId:X16} " +
            $"agent={(nint)agent:X}");
        if (agent == null) { tcs.TrySetResult(default); return; }
        // Mute before OpenCharaCard so the request-send sound is suppressed.
        TryMuteSound();
        Interlocked.Exchange(ref _hardCutoff, Environment.TickCount64 + HardCutoffMs);
        agent->OpenCharaCard(contentId);
    }

    public async Task<CharaCardResult> RequestAsync(ulong contentId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Delay(ThrottleMs, ct);

            var tcs = new TaskCompletionSource<CharaCardResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(ref _tcs, tcs);
            Interlocked.Exchange(ref _pendingCid, (long)contentId);

            Plugin.Log.Debug($"[CharaCardLookup] requesting {contentId:X16}");

            await Plugin.Framework.RunOnFrameworkThread(
                () => SendOpenRequest(contentId, tcs));

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                Plugin.Log.Debug(
                    $"[CharaCardLookup] resolved {contentId:X16} → " +
                    $"name={result.Name} job={result.ClassJobId}");
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Plugin.Log.Debug($"[CharaCardLookup] timeout {contentId:X16}");
                return default;
            }
        }
        finally
        {
            // Enter cleanup grace window BEFORE clearing _pendingCid so the
            // active-state never flips false. OnFrameworkUpdate keeps hiding any
            // late-arriving addon and only unmutes once this window expires.
            ExtendCleanup(ref _cleanupUntil, Environment.TickCount64 + CleanupGraceMs);
            Interlocked.Exchange(ref _pendingCid, 0);
            Volatile.Write(ref _tcs, null);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnCharaCardEarly);
        Plugin.ToastGui.ErrorToast -= OnErrorToast;
        Plugin.ToastGui.Toast      -= OnToast;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update    -= OnFrameworkUpdate;
        TryUnmuteSound();
        _hook.Disable();
        _hook.Dispose();
        _gate.Dispose();
    }
}
