using BOCCHI.Data;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.ForkedTower;

public class Panel
{
    public void Draw(ForkedTowerModule module)
    {
        if (!ZoneData.IsInForkedTower())
        {
            return;
        }

        OcelotUi.Title(Ocelot.I18N.T("ui.forked_tower"));
        OcelotUi.Indent(() =>
        {
            var state = OcelotUi.LabelledValue(Ocelot.I18N.T("ui.tower_id"), module.TowerRun.Hash);
            if (state == UiState.Hovered)
            {
                ImGui.SetTooltip(Ocelot.I18N.T("ui.this_is_unique_to_you"));
            }
        });
    }
}
