using System.Linq;
using BOCCHI.Data;
using BOCCHI.Enums;
using BOCCHI.Modules.Teleporter;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class TeleporterPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.teleporter");
    }

    public override void Render(DebugModule module)
    {
        if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
        {
            OcelotUi.Title(Ocelot.I18N.T("ui.teleporter_2"));
            OcelotUi.Indent(() =>
            {
                var shards = ZoneData.GetNearbyAethernetShards();
                if (shards.Count > 0)
                {
                    OcelotUi.Title(Ocelot.I18N.T("ui.nearby_aethernet_shards"));
                    OcelotUi.Indent(() =>
                    {
                        foreach (var shard in ZoneData.GetNearbyAethernetShards())
                        {
                            var data = AethernetData.All().First(o => o.DataId == shard.DataId);
                            ImGui.TextUnformatted(data.Aethernet.ToFriendlyString());
                        }
                    });
                }

                if (ImGui.Button(Ocelot.I18N.T("ui.test_return")))
                {
                    teleporter.teleporter.Return();
                }
            });
        }
    }
}
