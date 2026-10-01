using System;
using System.Linq;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.Teleporter;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class CriticalEncountersPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.critical_encounters");
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Title(Ocelot.I18N.T("ui.critical_encounters_2"));
        OcelotUi.Indent(() =>
        {
            foreach (var data in EventData.CriticalEncounters.Values)
            {
                var ev = module.GetModule<CriticalEncountersModule>().CriticalEncounters[data.Id];

                ImGui.TextUnformatted(Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString()));

                if (ev.State == DynamicEventState.Inactive)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"(Inactive)");
                }

                if (ev.State == DynamicEventState.Register)
                {
                    var start = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimestamp).DateTime;
                    var timeUntilStart = start - DateTime.UtcNow;
                    var formattedTime = $"{timeUntilStart.Minutes:D2}:{timeUntilStart.Seconds:D2}";

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"(Preparing: {formattedTime})");
                }

                if (ev.State == DynamicEventState.Warmup)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"(Starting)");
                }

                if (ev.State == DynamicEventState.Battle)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({ev.Progress}%)");
                }

                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    var start = ev.MapMarker.Position;

                    teleporter.teleporter.Button(data.Aethernet, start, Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString()), $"ce_{data.Id}", data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));

                if (data.Id != EventData.CriticalEncounters.Keys.Max())
                {
                    OcelotUi.VSpace();
                }

                if (ImGui.CollapsingHeader(Ocelot.I18N.T("ui.event_data") + $"##{data.Id}"))
                {
                    PrintEvent(ev);
                }

                if (ImGui.CollapsingHeader(Ocelot.I18N.T("ui.map_marker_data") + $"##{data.Id}"))
                {
                    PrintMapMarker(ev.MapMarker);
                }
            }
        });
    }

    private unsafe void PrintEvent(DynamicEvent ev)
    {
        OcelotUi.Title(Ocelot.I18N.T("ui.name_offset"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.NameOffset.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.description_offset"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DescriptionOffset.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.lgb_event_object"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.LGBEventObject.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.lgb_map_range"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.LGBMapRange.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.quest_rowid"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Quest.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.announce_rowid"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Announce.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown0"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown0.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown1"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown1.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown6"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown6.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown7"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown7.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown2"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown2.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.event_type_rowid"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.EventType.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.enemy_type_rowid"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.EnemyType.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.max_participants"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.MaxParticipants.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.radius"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown4.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown5"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown5.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.single_battle_rowid"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SingleBattle.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.unknown8"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Unknown8.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.start_timestamp"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.StartTimestamp.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.seconds_left"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SecondsLeft.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.seconds_duration"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.SecondsDuration.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.dynamic_event_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DynamicEventId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.dynamic_event_type"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.DynamicEventType.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.state"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.State.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.participants"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Participants.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.progress_2"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Progress.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.name"));
        ImGui.SameLine();
        ImGui.TextUnformatted(Localization.GameName("DynamicEvent", ev.DynamicEventId, ev.Name.ToString()));

        OcelotUi.Title(Ocelot.I18N.T("ui.description"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.Description.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.icon_objective_0"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.IconObjective0.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.max_participants_2"));
        ImGui.SameLine();
        ImGui.TextUnformatted(ev.MaxParticipants2.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.map_marker"));
        ImGui.SameLine();
        ImGui.TextUnformatted(
            $"X: {ev.MapMarker.Position.X}, Y: {ev.MapMarker.Position.Y}, {Ocelot.I18N.T("ui.icon_id")}: {ev.MapMarker.IconId}"); // example, adjust fields accordingly

        OcelotUi.Title(Ocelot.I18N.T("ui.event_container_pointer"));
        ImGui.SameLine();
        ImGui.TextUnformatted(((IntPtr)ev.EventContainer).ToString("X"));
    }


    private unsafe void PrintMapMarker(MapMarkerData marker)
    {
        OcelotUi.Title(Ocelot.I18N.T("ui.level_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.LevelId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.objective_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.ObjectiveId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.tooltip_string"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.TooltipString != null ? marker.TooltipString->ToString() : Ocelot.I18N.T("ui.null"));

        OcelotUi.Title(Ocelot.I18N.T("ui.icon_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.IconId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.position_x"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.X.ToString("F2"));

        OcelotUi.Title(Ocelot.I18N.T("ui.position_y"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.Y.ToString("F2"));

        OcelotUi.Title(Ocelot.I18N.T("ui.position_z"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Position.Z.ToString("F2"));

        OcelotUi.Title(Ocelot.I18N.T("ui.radius_2"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Radius.ToString("F2"));

        OcelotUi.Title(Ocelot.I18N.T("ui.map_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.MapId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.place_name_zone_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.PlaceNameZoneId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.place_name_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.PlaceNameId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.end_timestamp"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.EndTimestamp.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.recommended_level"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.RecommendedLevel.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.territory_type_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.TerritoryTypeId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.data_id"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.DataId.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.marker_type"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.MarkerType.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.event_state"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.EventState.ToString());

        OcelotUi.Title(Ocelot.I18N.T("ui.flags"));
        ImGui.SameLine();
        ImGui.TextUnformatted(marker.Flags.ToString());
    }
}
