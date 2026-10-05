using System;
using System.Linq;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Fates;
using BOCCHI.Modules.StateManager;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public class Automator
{
    private static bool IsChainActive
    {
        get => ChainManager.Queues.Count > 0;
    }

    public Activity? Activity { get; private set; } = null;

    // Time we started idling away from the return position, only counted while that lasts without a break
    private long idleSince = 0;

    private long lastIdleTick = 0;

    private const int IDLE_RETURN_DELAY_MS = 3000;

    public void PostUpdate(AutomatorModule module, IFramework framework)
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var lifestream = module.GetIPCSubscriber<Lifestream>();
        if (!vnav.IsReady() || !lifestream.IsReady())
        {
            return;
        }

        var states = module.GetModule<StateManagerModule>();
        if (Activity == null)
        {
            if (states.GetState() == State.InCombat)
            {
                return;
            }

            if (states.GetState() == State.InCriticalEncounter)
            {
                var critical = module.GetModule<CriticalEncountersModule>();

                // Right after an encounter ends the state can still read InCriticalEncounter for a few frames while every encounter is already inactive
                var active = critical.CriticalEncounters.Values.Where(ev => ev.State != DynamicEventState.Inactive).ToList();
                if (active.Count == 0 || !EventData.CriticalEncounters.TryGetValue(active[^1].DynamicEventId, out var data))
                {
                    return;
                }

                Activity = new CriticalEncounter(data, lifestream, vnav, module, critical);
                module.Debug($"Resuming running activity: {Activity.GetName()}");

                return;
            }

            if (states.GetState() == State.InFate)
            {
                Activity ??= FindFate(module, lifestream, vnav);

                if (Activity != null)
                {
                    module.Debug($"Resuming running activity: {Activity.GetName()}");
                }

                return;
            }
        }

        if (Activity != null && !Activity.IsValid())
        {
            Plugin.Chain.Abort();
            vnav.Stop();
            Activity = null;
        }

        if (IsChainActive)
        {
            return;
        }

        if (Activity != null)
        {
            if (Activity.state == ActivityState.Done)
            {
                Activity = null;
                return;
            }

            var chain = Activity.GetChain(states);
            if (chain == null)
            {
                return;
            }

            Plugin.Chain.Submit(chain);
            return;
        }

        if (!module.Config.ShouldDoFates && !module.Config.ShouldDoCriticalEncounters)
        {
            return;
        }

        // Try and get the next activity
        Activity ??= module.Config.ShouldDoCriticalEncounters ? FindCriticalEncounter(module, lifestream, vnav) : null;
        Activity ??= module.Config.ShouldDoFates ? FindFate(module, lifestream, vnav) : null;
        if (Activity != null)
        {
            Svc.Log.Info($"Selected activity: {Activity.GetName()}");
            return;
        }

        // With a custom return position, that is the only place we idle at, otherwise any aethernet shard will do
        var teleporterConfig = module.PluginConfig.TeleporterConfig;
        if (teleporterConfig.UseCustomReturnPosition)
        {
            if (Player.DistanceTo(teleporterConfig.GetReturnPosition()) <= teleporterConfig.CustomReturnIdleDistance)
            {
                return;
            }
        }
        else if (AethernetData.GetClosestToPlayer().DistanceToPlayer() <= 4.5f)
        {
            return;
        }

        // Any frame spent doing something else starts the count over, so a brief hiccup (like the state dropping
        // to idle for a moment while a critical encounter moves us into the arena) can't trigger a return on its own
        var now = Environment.TickCount64;
        if (now - lastIdleTick > 250)
        {
            idleSince = now;
        }

        lastIdleTick = now;
        if (now - idleSince > IDLE_RETURN_DELAY_MS)
        {
            idleSince = now;

            Plugin.Chain.Submit(ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true }));
        }
    }

    private static CriticalEncounter? FindCriticalEncounter(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<CriticalEncountersModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var encounter in source.CriticalEncounters.Values)
        {
            if (!module.Config.CriticalEncountersMap.TryGetValue(encounter.DynamicEventId, out var enabled) || !enabled)
            {
                continue;
            }

            if (encounter.State != DynamicEventState.Register)
            {
                continue;
            }

            if (!EventData.CriticalEncounters.TryGetValue(encounter.DynamicEventId, out var data))
            {
                continue;
            }

            return new CriticalEncounter(data, lifestream, vnav, module, source);
        }

        return null;
    }

    private static FateActivity? FindFate(AutomatorModule module, Lifestream lifestream, VNavmesh vnav)
    {
        if (!module.TryGetModule<FatesModule>(out var source) || source == null)
        {
            return null;
        }

        foreach (var fate in source.fates.Values)
        {
            // Fates the plugin has no data for have no toggle, so they are never automated
            if (!module.Config.FatesMap.TryGetValue(fate.Id, out var enabled) || !enabled)
            {
                continue;
            }

            return new FateActivity(fate.Data, lifestream, vnav, module, fate);
        }

        return null;
    }

    public void Refresh()
    {
        Activity = null;
        idleSince = 0;
        lastIdleTick = 0;
    }
}
