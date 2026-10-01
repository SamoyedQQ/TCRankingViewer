using Dalamud.Bindings.ImGui;
using Ocelot.Ui;
using Ocelot.Chain;

namespace BOCCHI.Modules.Debug.Panels;

public class ChainManagerPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.chain_manager");
    }

    public override void Render(DebugModule module)
    {
        OcelotUi.Title(Ocelot.I18N.T("ui.chain_manager_2"));
        OcelotUi.Indent(() =>
        {
            var instances = ChainManager.Queues;
            OcelotUi.Title(Ocelot.I18N.T("ui.of_instances"));
            ImGui.SameLine();
            ImGui.TextUnformatted(instances.Count.ToString());

            foreach (var pair in instances)
            {
                if (pair.Value.CurrentChain == null)
                {
                    continue;
                }

                OcelotUi.Title($"{pair.Key}:");
                OcelotUi.Indent(() =>
                {
                    var current = pair.Value.CurrentChain!;
                    OcelotUi.Title(Ocelot.I18N.T("ui.current_chain"));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(current.Name);

                    OcelotUi.Title(Ocelot.I18N.T("ui.progress_2"));
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{current.Progress * 100}%");

                    OcelotUi.Title(Ocelot.I18N.T("ui.queued_chains"));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(pair.Value.QueueCount.ToString());
                });
            }
        });
    }
}
