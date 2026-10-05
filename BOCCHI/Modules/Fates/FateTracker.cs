using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

public class FateTracker
{
    public readonly Dictionary<uint, Fate> Fates = [];

    public event Action<Fate>? OnFateSpawned;

    public event Action<Fate>? OnFateDespawned;


    public void Update(UpdateContext context)
    {
        var currentFates = Svc.Fates.ToDictionary(f => (uint)f.FateId, f => f);

        foreach (var (id, data) in currentFates)
        {
            // Keep the existing instance so its progress history survives, just refresh its snapshot
            if (Fates.TryGetValue(id, out var existing))
            {
                existing.Refresh(data);
                continue;
            }

            var fate = new Fate(data);
            Fates[id] = fate;
            OnFateSpawned?.Invoke(fate);
        }

        var despawned = Fates.Keys.Except(currentFates.Keys).ToList();
        foreach (var id in despawned)
        {
            OnFateDespawned?.Invoke(Fates[id]);
            Fates.Remove(id);
        }

        foreach (var fate in Fates.Values)
        {
            fate.Update(context);
        }
    }
}
