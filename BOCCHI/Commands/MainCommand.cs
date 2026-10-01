using System.Collections.Generic;
using BOCCHI.Modules.Debug;
using ECommons;
using ECommons.DalamudServices;
using Ocelot;
using Ocelot.Commands;
using Ocelot.Modules;

namespace BOCCHI.Commands;

[OcelotCommand]
public class MainCommand(Plugin plugin) : OcelotCommand
{
    protected override string Command
    {
        get => "/bocchi";
    }

    protected override string Description
    {
        get => Ocelot.I18N.T("ui.commands_main_help");
    }

    protected override IReadOnlyList<string> Aliases
    {
        get => ["/och", "/occultcrescenthelper"];
    }

    public override void Execute(string command, string arguments)
    {
        if (arguments is "config" or "cfg")
        {
            plugin.Windows.ToggleConfigUI();
            return;
        }

#if DEBUG_BUILD
        if (arguments == "debug")
        {
            plugin.Windows.GetWindow<DebugWindow>().Toggle();
            return;
        }
#endif

        if (arguments == "buff")
        {
            new BuffCommand(plugin).Execute("/bocchibuff", "");
            return;
        }

        if (arguments.StartsWith("tp"))
        {
            new TeleportCommand(plugin).Execute("/bocchitp", arguments.ReplaceFirst("tp", "").Trim());
            return;
        }

        if (arguments.StartsWith("language"))
        {
            var parts = arguments.Split(' ', 2);
            if (parts.Length == 2)
            {
                var code = Localization.Normalize(parts[1].Trim());
                if (code != null)
                {
                    plugin.Config.Language = code;
                    Localization.SetLanguage(code);
                    plugin.Config.Save();
                    Svc.Chat.Print(string.Format(Ocelot.I18N.T("ui.language_set_to_0"), code));
                    return;
                }

                Svc.Chat.Print(string.Format(Ocelot.I18N.T("ui.unknown_language_code_0"), parts[1].Trim()));
                return;
            }

            Svc.Chat.Print(Ocelot.I18N.T("ui.usage_bocchi_language_code"));
            return;
        }

        plugin.Windows.ToggleMainUI();
    }
}
