using System.Numerics;
using BOCCHI.Data;
using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Teleporter;

public class TeleporterConfig : ModuleConfig
{
    [Checkbox]
    [RequiredPlugin("Lifestream")]
    [IllegalModeCompatible]

    public bool ShouldMount { get; set; } = true;

    [Checkbox]
    [Illegal]
    [RequiredPlugin("vnavmesh")]

    public bool PathToDestination { get; set; } = false;

    [Checkbox] public bool ReturnAfterFate { get; set; } = false;

    [Checkbox] public bool ReturnAfterCriticalEncounter { get; set; } = false;

    [Checkbox]
    [RequiredPlugin("vnavmesh")]

    public bool ApproachAetheryte { get; set; } = false;

    // Custom return position, drawn manually in TeleporterModule.RenderConfigUi
    public bool UseCustomReturnPosition { get; set; } = false;

    public float CustomReturnX { get; set; } = 0f;

    public float CustomReturnY { get; set; } = 0f;

    public float CustomReturnZ { get; set; } = 0f;

    // How close to the custom return position counts as arrived
    public float CustomReturnArriveDistance { get; set; } = 1f;

    // Idle tolerance is a little looser than arrival so the automator doesn't re-return in a loop
    public float CustomReturnIdleDistance
    {
        get => CustomReturnArriveDistance + 0.5f;
    }

    public Vector3 GetReturnPosition()
    {
        if (UseCustomReturnPosition)
        {
            return new Vector3(CustomReturnX, CustomReturnY, CustomReturnZ);
        }

        return ZoneData.Aetherytes[ZoneData.SOUTHHORN];
    }

    public void SetCustomReturnPosition(Vector3 position)
    {
        UseCustomReturnPosition = true;
        CustomReturnX = position.X;
        CustomReturnY = position.Y;
        CustomReturnZ = position.Z;
    }

    public void ResetReturnPosition()
    {
        UseCustomReturnPosition = false;
    }
}
