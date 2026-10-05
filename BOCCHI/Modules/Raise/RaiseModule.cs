using System;
using BOCCHI.Data;
using ECommons;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot.Modules;

namespace BOCCHI.Modules.Raise;

[OcelotModule(7)]
public class RaiseModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override RaiseConfig Config
    {
        get => PluginConfig.RaiseConfig;
    }

    // Give the prompt a moment before accepting, like someone reading it would
    private const int ACCEPT_DELAY_MS = 1000;

    private long promptSeenAt = 0;

    public override unsafe void Update(UpdateContext context)
    {
        // The raise prompt only shows while someone's raise is pending on us, which is what the Raise status marks.
        // Checking for it keeps us from answering the return to home point prompt that is also up while dead.
        if (!Config.AutoAcceptRaise || !ZoneData.IsInOccultCrescent() || !Player.Available || !Player.IsDead ||
            !Player.Status.HasAny(PlayerStatus.Raise, PlayerStatus.RaiseAlternate))
        {
            promptSeenAt = 0;
            return;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) || !GenericHelpers.IsAddonReady(addon))
        {
            promptSeenAt = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (promptSeenAt == 0)
        {
            promptSeenAt = now;
            return;
        }

        if (now - promptSeenAt < ACCEPT_DELAY_MS || !EzThrottler.Throttle("RaiseModule.Accept", 2000))
        {
            return;
        }

        Debug("Accepting raise");
        addon->FireCallbackInt(0);
    }
}
