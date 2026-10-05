using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Raise;

public class RaiseConfig : ModuleConfig
{
    [Checkbox] [IllegalModeCompatible] public bool AutoAcceptRaise { get; set; } = false;
}
