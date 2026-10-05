using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.IPC;
using System;
using System.Linq;
using System.Numerics;

namespace BOCCHI.Modules.Automator;

public class CriticalEncounter : Activity
{
    private readonly CriticalEncountersModule source;

    private DynamicEvent Encounter
    {
        get => source.CriticalEncounters[data.Id];
    }

    private bool finalDestination = false;

    public CriticalEncounter(EventData data, Lifestream lifestream, VNavmesh vnav, AutomatorModule module, CriticalEncountersModule source)
        : base(data, lifestream, vnav, module)
    {
        this.source = source;

        handlers.Add(ActivityState.WaitingToStartCriticalEncounter, GetWaitingToStartCriticalEncounterChain);
    }

    protected override TaskManagerTask GetPathfindingWatcher(StateManagerModule states)
    {
        return new TaskManagerTask(() =>
        {
            if (!IsValid())
            {
                throw new Exception("Activity is no longer valid.");
            }

            if (!finalDestination && IsCloseToZone())
            {
                var destination = GetEdgeDestination();
                if (destination != null && vnav.PathfindAndMoveTo(destination.Value, false))
                {
                    finalDestination = true;
                }
            }

            if (!finalDestination && IsInZone())
            {
                if (vnav.IsRunning())
                {
                    vnav.Stop();
                }

                return true;
            }

            var critical = module.GetModule<CriticalEncountersModule>();
            var encounter = critical.CriticalEncounters[data.Id];

            if (encounter.State != DynamicEventState.Register)
            {
                throw new Exception("This event started without you");
            }

            if (finalDestination)
            {
                return !vnav.IsRunning();
            }

            if (!vnav.IsRunning())
            {
                throw new VnavmeshStoppedException();
            }

            return false;
        }, new TaskManagerConfiguration { TimeLimitMS = 180000, ShowError = false });
    }


    private Func<Chain> GetWaitingToStartCriticalEncounterChain(StateManagerModule states)
    {
        return () =>
        {
            return Chain.Create("Illegal:WaitingToStartCriticalEncounter")
                .Then(new TaskManagerTask(() =>
                    {
                        if (!IsValid())
                        {
                            throw new Exception("The critical encounter appears to have started without you.");
                        }

                        var critical = module.GetModule<CriticalEncountersModule>();
                        var encounter = critical.CriticalEncounters[data.Id];

                        if (encounter.State == DynamicEventState.Battle &&
                            states.GetState() != State.InCriticalEncounter)
                        {
                            throw new Exception("The critical encounter appears to have started without you.");
                        }

                        if (!vnav.IsRunning() && states.GetState() == State.InCombat)
                        {
                            Actions.TryUnmount();

                            if (module.Config.ShouldToggleAiProvider)
                            {
                                module.Config.AiProvider.On();
                            }
                        }

                        return states.GetState() == State.InCriticalEncounter;
                    },
                    new TaskManagerConfiguration
                    {
                        TimeLimitMS = 180000,
                    }))
                .Then(_ => state = ActivityState.Participating);
        };
    }

    public override unsafe bool IsValid()
    {
        if (Encounter.State == DynamicEventState.Register)
        {
            return true;
        }

        var dec = DynamicEventContainer.GetInstance();
        return dec != null && Encounter.DynamicEventId == dec->CurrentEventId;
    }

    private const float FALLBACK_RADIUS = 20f;

    protected override float GetRadius()
    {
        // The map marker radius is what the game draws as the dotted circle around the encounter
        var markerRadius = Encounter.MapMarker.Radius;
        if (markerRadius > 5f)
        {
            return markerRadius;
        }

        // This is kind of an assumption, but it seems accurate enough for most encounters.
        var unknownRadius = Encounter.Unknown4;
        if (unknownRadius > 5f)
        {
            return unknownRadius;
        }

        return FALLBACK_RADIUS;
    }

    protected override Vector3 GetPosition()
    {
        return Encounter.MapMarker.Position;
    }

    public override string GetName()
    {
        return Localization.GameName("DynamicEvent", Encounter.DynamicEventId, Encounter.Name.ToString());
    }

    private bool IsCloseToZone(float radius = 50f)
    {
        return Player.DistanceTo(GetPosition()) <= radius;
    }

    // The registration area turned out a little smaller than the map marker radius, so keep a few yalms of margin
    private const float EDGE_MARGIN_MIN = 3f;

    private const float EDGE_MARGIN_MAX = 4.5f;

    // Pick a spot near the edge of the zone, preferably next to other players waiting there, so we blend in with the crowd.
    private Vector3? GetEdgeDestination()
    {
        var center = GetPosition();
        var radius = data.Radius ?? GetRadius();
        module.Debug($"Encounter radius: {radius:F1} (marker: {Encounter.MapMarker.Radius:F1}, unknown4: {Encounter.Unknown4:F1})");

        // Stand just inside the boundary: 3-4.5 yalms in from the edge, but on small zones stay between 70% and 75% of the radius
        var outerEdge = MathF.Max(radius - EDGE_MARGIN_MIN, radius * 0.75f);
        var innerEdge = MathF.Min(outerEdge, MathF.Max(radius - EDGE_MARGIN_MAX, radius * 0.7f));
        var distanceFromCenter = innerEdge + (float)Random.Shared.NextDouble() * (outerEdge - innerEdge);

        var players = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.Player && o.Address != Player.Object.Address)
            .Where(o => Distance2D(o.Position, center) <= radius)
            .ToList();

        var edgePlayers = players.Where(p => Distance2D(p.Position, center) >= radius * 0.75f).ToList();

        float angle;
        float height;
        if (edgePlayers.Count > 0)
        {
            // Stand next to one of the few players at the edge closest to our approach, rather than crossing the whole zone
            var anchor = edgePlayers
                .OrderBy(p => Player.DistanceTo(p.Position))
                .Take(3)
                .ElementAt(Random.Shared.Next(Math.Min(3, edgePlayers.Count)))
                .Position;

            // Shift sideways along the edge by 1.5-3 yalms so we don't stand inside them
            var sideways = (1.5f + (float)Random.Shared.NextDouble() * 1.5f) * (Random.Shared.Next(2) == 0 ? -1f : 1f);
            angle = MathF.Atan2(anchor.Z - center.Z, anchor.X - center.X) + sideways / distanceFromCenter;
            height = anchor.Y;
        }
        else
        {
            // Nobody at the edge yet: stop at the edge on the side we are approaching from
            angle = MathF.Atan2(Player.Position.Z - center.Z, Player.Position.X - center.X) + (float)((Random.Shared.NextDouble() - 0.5) * 0.6);
            height = Player.Position.Y;
        }

        var target = new Vector3(center.X + MathF.Cos(angle) * distanceFromCenter, height, center.Z + MathF.Sin(angle) * distanceFromCenter);
        target = vnav.FindPointOnFloor(target, false, 0.5f) ?? target;

        module.Debug($"Pathfinding to edge point: {target} ({Distance2D(target, center):F1}/{radius:F1} from center, {players.Count} players in zone)");

        return target;
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
    }


    protected override unsafe bool IsActivityTarget(IBattleNpc obj)
    {
        try
        {
            var battleChara = (BattleChara*)obj.Address;

            var isRelatedToCurrentEvent = battleChara->EventId.EntryId == Player.BattleChara->EventId.EntryId;

            return obj.SubKind == (byte)BattleNpcSubKind.Enemy && isRelatedToCurrentEvent;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }

    protected override ActivityState GetPostPathfindingState()
    {
        return ActivityState.WaitingToStartCriticalEncounter;
    }

    // The current event id can drop out for a moment, e.g. while the encounter moves us into the arena, so only stop once it stays gone
    private const int PARTICIPATION_END_GRACE_MS = 3000;

    private long idleSince = 0;

    protected override bool IsParticipationOver(StateManagerModule states)
    {
        if (states.GetState() != State.Idle)
        {
            idleSince = 0;
            return false;
        }

        var now = Environment.TickCount64;
        if (idleSince == 0)
        {
            idleSince = now;
        }

        return now - idleSince >= PARTICIPATION_END_GRACE_MS;
    }

    protected override ActivityState GetPostParticipatingState()
    {
        idleSince = 0;

        // We left the registration area before it closed, head back in instead of giving up on the encounter
        if (Encounter.State == DynamicEventState.Register)
        {
            module.Debug("Dropped out of the critical encounter registration area, heading back in");
            finalDestination = false;
            return ActivityState.Idle;
        }

        return ActivityState.Done;
    }
}
