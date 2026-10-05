using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Fates;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

// Holds a snapshot of the game's fate data instead of the IFate itself.
// IFate points straight into game memory, which is freed as soon as the fate ends; reading it afterwards
// (e.g. while drawing the UI) throws an AccessViolationException, which .NET cannot catch and crashes the game.
public class Fate
{
    public readonly EventData Data;

    public uint Id { get; }

    public string Name { get; private set; } = string.Empty;

    private float gameRadius;

    private Vector3 gamePosition;

    public byte CurrentProgress { get; private set; }

    public readonly EventProgress Progress = new();

    public Fate(IFate fate)
    {
        Id = fate.FateId;
        Data = EventData.Fates.TryGetValue(Id, out var data) ? data : new EventData { Id = Id, Type = EventType.Fate };
        Refresh(fate);
    }

    public float Radius
    {
        get => Data.Radius ?? gameRadius;
    }

    public Vector3 StartPosition
    {
        get => Data.StartPosition ?? gamePosition;
    }

    // Only call this with a fate taken from the current Svc.Fates, while it is guaranteed to still exist
    public void Refresh(IFate fate)
    {
        Name = Localization.GameName("Fate", Id, fate.Name.ToString());
        gameRadius = fate.Radius;
        gamePosition = fate.Position;
        CurrentProgress = fate.Progress;
    }

    public void Update(UpdateContext context)
    {
        if (CurrentProgress <= 0)
        {
            return;
        }

        if (Progress.Count == 0 || Progress.Latest != CurrentProgress)
        {
            Progress.Add(CurrentProgress);
        }
    }

    public bool IsPotFate()
    {
        return Data.Note == MonsterNote.PersistentPots;
    }

    public Aethernet GetAethernet()
    {
        return Data.Aethernet ?? ZoneData.GetClosestAethernetShard(StartPosition);
    }
}
