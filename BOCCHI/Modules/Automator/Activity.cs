using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BOCCHI.Chains;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Ocelot.Chain;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public abstract class Activity
{
    public readonly EventData data;

    private readonly Lifestream lifestream;

    protected readonly VNavmesh vnav;

    protected readonly AutomatorModule module;

    public ActivityState state = ActivityState.Idle;

    protected readonly Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>> handlers;

    protected Activity(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module)
    {
        this.data = data;
        this.lifestream = lifestream;
        this.vnav = vnav;
        this.module = module;

        handlers = new Dictionary<ActivityState, Func<StateManagerModule, Func<Chain>?>>
        {
            { ActivityState.Idle, GetIdleChain },
            { ActivityState.Pathfinding, GetPathfindingChain },
            { ActivityState.Participating, GetParticipatingChain },
            { ActivityState.Done, GetDoneChain },
        };

        var states = module.GetModule<StateManagerModule>();
        if (states.GetState() == State.InFate || states.GetState() == State.InCriticalEncounter)
        {
            state = ActivityState.Participating;
        }
    }


    public Func<Chain>? GetChain(StateManagerModule states)
    {
        return !IsValid() ? null : handlers[state](states);
    }

    private Func<Chain> GetIdleChain(StateManagerModule states)
    {
        return () =>
        {
            bool ShouldToggleAi(ChainContext _)
            {
                return module.Config.ShouldToggleAiProvider && !Svc.Condition[ConditionFlag.InCombat];
            }

            return Chain.Create("Illegal:Idle")
                .ConditionalThen(ShouldToggleAi, _ => module.Config.AiProvider.Off())
                .Then(_ => vnav.Stop())
                .Then(_ => state = ActivityState.Pathfinding);
        };
    }

    private const int NAVIGATION_ESTIMATE_TIMEOUT_MS = 5000;

    private Func<Chain> GetPathfindingChain(StateManagerModule states)
    {
        return () =>
        {
            var playerShard = AethernetData.AllByDistance().First();
            var activityShard = GetAethernetData();

            var isFate = data.Type == EventType.Fate;

            Task<NavigationEstimate>? estimate = null;
            var estimateStarted = 0L;
            NavigationType? navType = null;

            bool Is(NavigationType type)
            {
                return navType == type;
            }

            return Chain.Create("Illegal:Pathfinding")
                .ConditionalWait(_ => !isFate && module.Config.ShouldDelayCriticalEncounters, Random.Shared.Next(10000, 15001))
                .Then(new TaskManagerTask(() =>
                {
                    // Measure walking distances along the navmesh, straight lines badly underestimate walks across cliffs and plateaus
                    if (estimate == null)
                    {
                        estimate = SmartNavigation.Estimate(vnav, Player.Position, GetPosition(), activityShard);
                        estimateStarted = Environment.TickCount64;
                    }

                    return estimate.IsCompleted || Environment.TickCount64 - estimateStarted > NAVIGATION_ESTIMATE_TIMEOUT_MS;
                }, new TaskManagerConfiguration { TimeLimitMS = NAVIGATION_ESTIMATE_TIMEOUT_MS + 5000 }))
                .Then(_ =>
                {
                    NavigationEstimate distances;
                    if (estimate is { IsCompletedSuccessfully: true })
                    {
                        distances = estimate.Result;
                    }
                    else
                    {
                        module.Debug("Navmesh distance estimate unavailable, falling back to straight line distances");
                        distances = SmartNavigation.EstimateStraightLine(Player.Position, GetPosition(), activityShard);
                    }

                    navType = SmartNavigation.Decide(distances, activityShard);
                    module.Debug("Selected navigation type: " + navType);
                })
                .ConditionalThen(_ => Is(NavigationType.ReturnWalk), ChainHelper.ReturnChain())
                .ConditionalThen(_ => Is(NavigationType.ReturnTeleportWalk),
                    ChainHelper.ReturnChain(new ReturnChainConfig { ApproachAetheryte = true }))
                .ConditionalThen(_ => Is(NavigationType.WalkTeleportWalk), ChainHelper.PathfindToAndWait(playerShard.Position, AethernetData.DISTANCE))
                .ConditionalThen(_ => Is(NavigationType.ReturnTeleportWalk) || Is(NavigationType.WalkTeleportWalk), ChainHelper.TeleportChain(activityShard.Aethernet))
                .ConditionalThen(_ => Is(NavigationType.ReturnTeleportWalk) || Is(NavigationType.WalkTeleportWalk),
                    new TaskManagerTask(() => !lifestream.IsBusy(), new TaskManagerConfiguration { TimeLimitMS = 30000 }))
                .Then(new PathfindingChain(vnav, GetPosition(), data))
                .ConditionalThen(_ => ShouldMountToPathfindTo(GetPosition()), ChainHelper.MountChain())
                .Then(GetPathfindingWatcher(states))
                .Then(_ => state = GetPostPathfindingState());
        };
    }


    private Func<Chain> GetParticipatingChain(StateManagerModule states)
    {
        return () =>
        {
            return Chain.Create("Illegal:Participating")
                .ConditionalThen(_ => module.Config.ShouldToggleAiProvider, _ => module.Config.AiProvider.On())
                .Then(_ => vnav.Stop())
                .Then(new TaskManagerTask(() =>
                {
                    if (!module.Config.ShouldForceTarget || !EzThrottler.Throttle("Participating.ForceTarget", 500))
                    {
                        return states.GetState() == State.Idle;
                    }

                    var enemies = GetEnemies();
                    Svc.Targets.Target = module.Config.ShouldForceTargetCentralEnemy ? enemies.Centroid() : enemies.Closest();

                    return states.GetState() == State.Idle;
                }, new TaskManagerConfiguration { TimeLimitMS = int.MaxValue }))
                .Then(_ => state = ActivityState.Done);
        };
    }

    private Func<Chain>? GetDoneChain(StateManagerModule states)
    {
        return null;
    }

    protected List<IBattleNpc> GetEnemies()
    {
        return TargetHelper.Enemies.Where(IsActivityTarget).ToList();
    }

    protected abstract bool IsActivityTarget(IBattleNpc obj);

    private AethernetData GetAethernetData()
    {
        return data.Aethernet?.GetData() ?? AethernetData.AllByDistance(GetPosition()).First();
    }

    protected bool IsInZone()
    {
        var radius = data.Radius ?? GetRadius();

        return Player.DistanceTo(GetPosition()) <= radius;
    }

    private bool ShouldMountToPathfindTo(Vector3 destination)
    {
        if (!module.PluginConfig.TeleporterConfig.ShouldMount)
        {
            return false;
        }

        return Vector3.Distance(Player.Position, destination) > 20f;
    }

    protected abstract float GetRadius();

    protected abstract TaskManagerTask GetPathfindingWatcher(StateManagerModule states);

    public abstract bool IsValid();

    protected abstract Vector3 GetPosition();

    public abstract string GetName();

    protected abstract ActivityState GetPostPathfindingState();
}
