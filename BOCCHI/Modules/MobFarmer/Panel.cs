using System.Linq;
using Dalamud.Bindings.ImGui;
using Ocelot;
using Ocelot.Ui;

namespace BOCCHI.Modules.MobFarmer;

public class Panel
{
    public void Draw(MobFarmerModule module)
    {
        OcelotUi.Title(Ocelot.I18N.T("ui.mob_farmer"));
        OcelotUi.Indent(() =>
        {
            if (ImGui.Button(module.Farmer.Running ? I18N.T("generic.label.stop") : I18N.T("generic.label.start")))
            {
                module.Farmer.Toggle(module);
            }

            if (module.Farmer.Running)
            {
                OcelotUi.LabelledValue(Ocelot.I18N.T("ui.phase"), Localization.State(module.Farmer.StateMachine.State));
            }

            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.not_engaged"), module.Scanner.NotInCombat.Count());
            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.engaged"), module.Scanner.InCombat.Count());
        });
    }
}
