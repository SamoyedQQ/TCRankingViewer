using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BOCCHI.Data;
using BOCCHI.Enums;
using ECommons.DalamudServices;
using Ocelot.IPC;

namespace BOCCHI.Modules.Automator;

public enum NavigationType
{
    Walk,
    ReturnWalk,
    ReturnTeleportWalk,
    WalkTeleportWalk,
}

// Walking distances along the navmesh for each leg the navigation options are built from
public sealed class NavigationEstimate
{
    public float WalkToEvent;

    public float WalkFromReturnToEvent;

    public float WalkFromEventShardToEvent;

    public float WalkToNearestShard;
}

public static class SmartNavigation
{
    private const float RETURN_BASE_COST = 75f;

    // Used when vnavmesh can't produce a path; terrain in the Occult Crescent rarely allows a straight line
    private const float FALLBACK_DETOUR_FACTOR = 1.4f;

    public static async Task<NavigationEstimate> Estimate(VNavmesh vnav, Vector3 playerPosition, Vector3 destination, AethernetData closestToDestination)
    {
        var closestToPlayer = AethernetData.GetClosestTo(playerPosition);
        var returnLanding = ZoneData.StartingLocations.TryGetValue(Svc.ClientState.TerritoryType, out var start)
            ? start
            : Aethernet.BaseCamp.GetData().Destination;

        var walkToEvent = PathLength(vnav, playerPosition, destination);
        var walkFromReturn = PathLength(vnav, returnLanding, destination);
        var walkFromShard = PathLength(vnav, closestToDestination.Destination, destination);
        var walkToShard = PathLength(vnav, playerPosition, closestToPlayer.Position);

        await Task.WhenAll(walkToEvent, walkFromReturn, walkFromShard, walkToShard);

        return new NavigationEstimate
        {
            WalkToEvent = walkToEvent.Result,
            WalkFromReturnToEvent = walkFromReturn.Result,
            WalkFromEventShardToEvent = walkFromShard.Result,
            WalkToNearestShard = walkToShard.Result,
        };
    }

    // Straight-line estimate, used if the navmesh estimate doesn't finish in time
    public static NavigationEstimate EstimateStraightLine(Vector3 playerPosition, Vector3 destination, AethernetData closestToDestination)
    {
        var closestToPlayer = AethernetData.GetClosestTo(playerPosition);
        var returnLanding = ZoneData.StartingLocations.TryGetValue(Svc.ClientState.TerritoryType, out var start)
            ? start
            : Aethernet.BaseCamp.GetData().Destination;

        return new NavigationEstimate
        {
            WalkToEvent = Vector3.Distance(playerPosition, destination) * FALLBACK_DETOUR_FACTOR,
            WalkFromReturnToEvent = Vector3.Distance(returnLanding, destination) * FALLBACK_DETOUR_FACTOR,
            WalkFromEventShardToEvent = Vector3.Distance(closestToDestination.Destination, destination) * FALLBACK_DETOUR_FACTOR,
            WalkToNearestShard = Vector3.Distance(playerPosition, closestToPlayer.Position) * FALLBACK_DETOUR_FACTOR,
        };
    }

    public static NavigationType Decide(NavigationEstimate estimate, AethernetData closestToDestination)
    {
        var costs = new Dictionary<NavigationType, float>
        {
            { NavigationType.Walk, estimate.WalkToEvent },
            { NavigationType.ReturnWalk, RETURN_BASE_COST + estimate.WalkFromReturnToEvent },
            { NavigationType.ReturnTeleportWalk, RETURN_BASE_COST + estimate.WalkFromEventShardToEvent },
            { NavigationType.WalkTeleportWalk, estimate.WalkToNearestShard + estimate.WalkFromEventShardToEvent },
        };

        Svc.Log.Debug("Closest Aethernet: " + closestToDestination.Aethernet.ToFriendlyString());
        foreach (var (type, cost) in costs)
        {
            Svc.Log.Debug($"{type} - {cost:f2}");
        }

        return costs.OrderBy(kv => kv.Value).First().Key;
    }

    private static async Task<float> PathLength(VNavmesh vnav, Vector3 from, Vector3 to)
    {
        var straight = Vector3.Distance(from, to);

        try
        {
            var path = await vnav.Pathfind(from, to, false);
            if (path == null || path.Count == 0)
            {
                return straight * FALLBACK_DETOUR_FACTOR;
            }

            var length = 0f;
            var previous = from;
            foreach (var point in path)
            {
                length += Vector3.Distance(previous, point);
                previous = point;
            }

            return MathF.Max(length, straight);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Unable to measure path length from {from} to {to}: {ex.Message}");
            return straight * FALLBACK_DETOUR_FACTOR;
        }
    }
}
