using System.Numerics;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;
using Ocelot.IPC;

namespace BOCCHI.Modules.Debug.Panels;

public class VnavmeshPanel : Panel
{
    public override string GetName()
    {
        return "Vnavmesh";
    }

    public override void Render(DebugModule module)
    {
        if (module.TryGetIPCSubscriber<VNavmesh>(out var vnav) && vnav!.IsReady())
        {
            OcelotUi.Title(Ocelot.I18N.T("ui.vnav_state"));
            ImGui.SameLine();
            ImGui.TextUnformatted(vnav.IsRunning() ? Ocelot.I18N.T("ui.running") : Ocelot.I18N.T("ui.pending"));


            if (ImGui.Button(Ocelot.I18N.T("ui.test_vnav_thingy")))
            {
                vnav.FollowPath([new Vector3(815.2f, 72.5f, -705.15f)], false);
            }
        }
    }
}
