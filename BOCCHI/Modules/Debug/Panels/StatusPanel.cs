using System.Linq;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class StatusPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.statuses");
    }

    public override void Render(DebugModule module)
    {
        var data = Svc.Data.GetExcelSheet<Status>();


        OcelotUi.Title(Ocelot.I18N.T("ui.statuses_2"));
        OcelotUi.Indent(() =>
        {
            foreach (var s in Svc.ClientState.LocalPlayer!.StatusList)
            {
                ImGui.TextUnformatted($"{data.Where(r => r.RowId == s.StatusId).First().Name} ({s.StatusId})");
            }
        });
    }
}
