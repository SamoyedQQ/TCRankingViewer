using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Buff;
using BOCCHI.Modules.Buff.Chains;
using BOCCHI.Modules.Teleporter;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using Ocelot.IPC;

namespace BOCCHI.Chains;

public class ReturnChain(TeleporterModule module, ReturnChainConfig config) : RetryChainFactory
{
    private bool complete = false;

    protected override Chain Create(Chain chain)
    {
        chain.BreakIf(() => Player.IsDead);

        var shouldReturn = GetCostToReturn() < GetCostToWalk();

        if (shouldReturn)
        {
            chain = Actions.Return.CastOnChain(chain);
            chain.WaitToCast().WaitToCycleCondition(ConditionFlag.BetweenAreas);
        }

        chain.Then(ChainHelper.TreasureSightChain());
        chain.Then(ApplyBuffs);

        if (config.ApproachAetheryte)
        {
            var vnav = module.GetIPCSubscriber<VNavmesh>();
            var lifestream = module.GetIPCSubscriber<Lifestream>();
            var position = GetReturnPosition();

            if (IsUsingCustomPosition())
            {
                chain.Then(new TaskManagerTask(() => ApproachCustomPosition(vnav, position), new TaskManagerConfiguration { TimeLimitMS = 60000 }));
            }
            else
            {
                chain.Then(new PathfindAndMoveToChain(vnav, position));
                chain.Then(_ => lifestream.GetActiveCustomAetheryte() != 0 && Player.DistanceTo(position) <= AethernetData.DISTANCE);
            }

            chain.Then(_ => vnav.Stop());
        }


        return chain.Then(_ => complete = true);
    }

    // Within this distance we walk straight at the custom position, the navmesh often leaves out the ground right next to the aetheryte
    private const float DIRECT_APPROACH_DISTANCE = 8f;

    private Vector3 lastApproachPosition;

    private long lastApproachProgress;

    private bool ApproachCustomPosition(VNavmesh vnav, Vector3 position)
    {
        var distance = Player.DistanceTo(position);
        if (distance <= module.Config.CustomReturnArriveDistance)
        {
            vnav.Stop();
            return true;
        }

        var now = Environment.TickCount64;
        if (Vector3.Distance(Player.Position, lastApproachPosition) > 0.3f)
        {
            lastApproachPosition = Player.Position;
            lastApproachProgress = now;
        }

        var stuck = vnav.IsRunning() && now - lastApproachProgress > 2000;
        if (stuck)
        {
            vnav.Stop();
        }

        // vnavmesh is still working on it
        if (!stuck && (vnav.IsRunning() || vnav.IsSimpleMoveInProgress()))
        {
            return false;
        }

        if (!EzThrottler.Throttle("ReturnChain.ApproachCustomPosition", 500))
        {
            return false;
        }

        lastApproachProgress = now;
        if (distance <= DIRECT_APPROACH_DISTANCE && !stuck)
        {
            vnav.MoveTo(new List<Vector3> { position }, false);
        }
        else
        {
            vnav.PathfindAndMoveTo(position, false);
        }

        return false;
    }

    private Chain ApplyBuffs()
    {
        var vnav = module.GetIPCSubscriber<VNavmesh>();
        var buffs = module.GetModule<BuffModule>();

        var closestKnowledgeCrystal = ZoneData.GetNearbyKnowledgeCrystal(60f).FirstOrDefault();

        var chain = Chain.Create();
        chain.BreakIf(() => !buffs.ShouldRefreshBuffs() || !vnav.IsReady() || closestKnowledgeCrystal == null);
        chain.Then(_ => Actions.TryUnmount());

        chain.PathfindAndMoveTo(vnav, closestKnowledgeCrystal!.Position);
        chain.WaitUntilNear(vnav, closestKnowledgeCrystal!.Position, AethernetData.DISTANCE);
        chain.Then(_ => vnav.Stop());

        chain.Then(new AllBuffsChain(buffs));

        return chain;
    }

    public override bool IsComplete()
    {
        return complete;
    }

    public override int GetMaxAttempts()
    {
        return 5;
    }

    public override TaskManagerConfiguration? Config()
    {
        return new TaskManagerConfiguration { TimeLimitMS = 60000 };
    }

    private Vector3 GetAetherytePosition()
    {
        if (ZoneData.Aetherytes.TryGetValue(Svc.ClientState.TerritoryType, out var position))
        {
            return position;
        }

        throw new Exception("Unable to determine Aetheryte position");
    }

    private bool IsUsingCustomPosition()
    {
        return config.UseCustomPosition && module.Config.UseCustomReturnPosition;
    }

    private Vector3 GetReturnPosition()
    {
        return IsUsingCustomPosition() ? module.Config.GetReturnPosition() : GetAetherytePosition();
    }

    private float GetCostToReturn()
    {
        if (ZoneData.StartingLocations.TryGetValue(Svc.ClientState.TerritoryType, out var start))
        {
            return Vector3.Distance(start, GetReturnPosition()) + 75f;
        }


        throw new Exception("Unable to determine Starting position");
    }

    private float GetCostToWalk()
    {
        return Player.DistanceTo(GetReturnPosition());
    }
}
