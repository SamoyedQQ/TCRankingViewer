using System;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.CriticalEncounters;

public class Panel
{
    public void Draw(CriticalEncountersModule module)
    {
        OcelotUi.Title($"{module.T("panel.title")}:");
        OcelotUi.Indent(() =>
        {
            var active = module.CriticalEncounters.Values.Count(ev => ev.State != DynamicEventState.Inactive);
            if (active <= 0)
            {
                ImGui.TextUnformatted(module.T("panel.none"));
                return;
            }

            foreach (var ev in module.CriticalEncounters.Values)
            {
                if (!ZoneData.IsInOccultCrescent())
                {
                    module.CriticalEncounters.Clear();
                    return;
                }

                if (ev.EventType >= 4)
                {
                    HandleTower(ev, module);
                    continue;
                }

                if (ev.State == DynamicEventState.Inactive)
                {
                    continue;
                }

                if (!EventData.CriticalEncounters.TryGetValue(ev.DynamicEventId, out var data))
                {
                    continue;
                }

                ImGui.TextUnformatted(Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString()));

                switch (ev.State)
                {
                    case DynamicEventState.Register:
                        {
                            var start = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimestamp).DateTime;
                            var timeUntilStart = start - DateTime.UtcNow;
                            var formattedTime = $"{timeUntilStart.Minutes:D2}:{timeUntilStart.Seconds:D2}";

                            ImGui.SameLine();
                            ImGui.TextUnformatted($"({module.T("panel.register")}: {formattedTime})");
                            break;
                        }
                    case DynamicEventState.Warmup:
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"({module.T("panel.warmup")})");
                        break;
                    case DynamicEventState.Battle:
                        {
                            ImGui.SameLine();
                            ImGui.TextUnformatted($"({ev.Progress}%)");

                            if (module.Progress.TryGetValue(ev.DynamicEventId, out var progress))
                            {
                                var estimate = progress.EstimateTimeToCompletion();
                                if (estimate != null)
                                {
                                    ImGui.SameLine();
                                    ImGui.TextUnformatted($"({module.T("panel.estimated")} {estimate.Value:mm\\:ss})");
                                }
                            }

                            break;
                        }
                    case DynamicEventState.Inactive:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (ev.State != DynamicEventState.Register)
                {
                    OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));
                    continue;
                }

                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    var start = ev.MapMarker.Position;

                    teleporter.teleporter.Button(data.Aethernet, start, Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString()), $"ce_{ev.DynamicEventId}", data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));
            }
        });
    }


    private void HandleTower(DynamicEvent ev, CriticalEncountersModule module)
    {
        if (!module.Config.TrackForkedTower || ev.State == DynamicEventState.Battle)
        {
            return;
        }

        OcelotUi.Error(Ocelot.I18N.T("ui.this_feature_is_a_work_in_progress"));

        if (ev.State == DynamicEventState.Inactive)
        {
            ImGui.TextUnformatted($"{Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString())}:");

            var time = module.Tracker.TowerTimer.GetTimeToForkedTowerSpawn(ev.State);
            OcelotUi.Indent(() => { OcelotUi.LabelledValue(Ocelot.I18N.T("ui.forked_tower_spawn_estimate"), $"{time:mm\\:ss}"); });
        }
        else
        {
            ImGui.TextUnformatted($"{Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString())}:");

            var time = module.Tracker.TowerTimer.GetTimeRemainingToRegister(ev.State);
            OcelotUi.Indent(() => { OcelotUi.LabelledValue(Ocelot.I18N.T("ui.forked_tower_register"), $"{time:mm\\:ss}"); });
        }

        OcelotUi.Indent(32, () =>
        {
            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.critical_encounters_completed"), module.Tracker.TowerTimer.CriticalEncountersCompleted);
            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.fates_completed"), module.Tracker.TowerTimer.FatesCompleted);
        });


        if (!TowerHelper.IsPlayerNearTower(TowerHelper.TowerType.Blood))
        {
            return;
        }

        OcelotUi.Indent(() =>
        {
            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.players_on_platform"), TowerHelper.GetPlayersInTowerZone(TowerHelper.TowerType.Blood));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Ocelot.I18N.T("ui.this_includes_your_character"));
            }

            OcelotUi.LabelledValue(Ocelot.I18N.T("ui.players_near_platform"), TowerHelper.GetPlayersNearTowerZone(TowerHelper.TowerType.Blood));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Ocelot.I18N.T("ui.this_includes_your_character"));
            }
        });
    }
}
