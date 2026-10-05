using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.StateManager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot.Modules;
using Ocelot.Ui;
using Ocelot.Windows;

namespace BOCCHI.Modules.Teleporter;

[OcelotModule(2)]
public class TeleporterModule : Module
{
    public override TeleporterConfig Config
    {
        get => PluginConfig.TeleporterConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public readonly Teleporter teleporter;

    public TeleporterModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        teleporter = new Teleporter(this);

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup);
    }

    public override void Initialize()
    {
        var states = GetModule<StateManagerModule>();
        states.OnExitInFate += teleporter.OnFateEnd;
        states.OnExitInCriticalEncounter += teleporter.OnCriticalEncounterEnd;
    }

    public override void Dispose()
    {
        var states = GetModule<StateManagerModule>();
        states.OnExitInFate -= teleporter.OnFateEnd;
        states.OnExitInCriticalEncounter -= teleporter.OnCriticalEncounterEnd;

        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoPostSetup);
    }

    public bool IsReady()
    {
        return teleporter.IsReady();
    }

    public override void RenderConfigUi(RenderContext context)
    {
        base.RenderConfigUi(context);

        OcelotUi.VSpace();
        OcelotUi.Separator();
        OcelotUi.Title(Ocelot.I18N.T("modules.teleporter.return_position.title"));

        var playerPosition = Player.Available ? Player.Position.ToCoordinateString() : "-";
        OcelotUi.LabelledValue(Ocelot.I18N.T("modules.teleporter.return_position.player"), playerPosition);

        var source = Config.UseCustomReturnPosition
            ? Ocelot.I18N.T("modules.teleporter.return_position.custom")
            : Ocelot.I18N.T("modules.teleporter.return_position.default");
        OcelotUi.LabelledValue(Ocelot.I18N.T("modules.teleporter.return_position.current"), $"{Config.GetReturnPosition().ToCoordinateString()} ({source})");
        OcelotUi.LabelledValue(Ocelot.I18N.T("modules.teleporter.return_position.original"), ZoneData.Aetherytes[ZoneData.SOUTHHORN].ToCoordinateString());

        ImGui.BeginDisabled(!Player.Available || !ZoneData.IsInOccultCrescent());
        if (ImGui.Button(Ocelot.I18N.T("modules.teleporter.return_position.record")))
        {
            Config.SetCustomReturnPosition(Player.Position);
            PluginConfig.Save();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();

        ImGui.BeginDisabled(!Config.UseCustomReturnPosition);
        if (ImGui.Button(Ocelot.I18N.T("modules.teleporter.return_position.reset")))
        {
            Config.ResetReturnPosition();
            PluginConfig.Save();
        }

        ImGui.EndDisabled();

        var arriveDistance = Config.CustomReturnArriveDistance;
        ImGui.SetNextItemWidth(200 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(Ocelot.I18N.T("modules.teleporter.return_position.arrive_distance"), ref arriveDistance, 0.5f, 5f, "%.1f"))
        {
            Config.CustomReturnArriveDistance = arriveDistance;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            PluginConfig.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Ocelot.I18N.T("modules.teleporter.return_position.arrive_distance_tooltip"));
        }

        ImGui.TextWrapped(Ocelot.I18N.T("modules.teleporter.return_position.help"));
    }

    private unsafe void OnSelectYesnoPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!ZoneData.IsInOccultCrescent() || ZoneData.IsInForkedTower() || Player.IsDead)
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (!addon->IsVisible)
        {
            return;
        }

        // This could be the dumbest thing I've ever written, but that bar is low
        if (addon->AtkValues[7].Type != ValueType.Int || addon->AtkValues[7].Int != -1)
        {
            return;
        }

        addon->FireCallbackInt(0);
    }
}
