using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Enums;
using BOCCHI.Modules.Automator;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class ActivityTargetPanel : Panel
{
    private List<IGameObject> enemies = [];

    public override string GetName()
    {
        return Ocelot.I18N.T("ui.activity_targets");
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            if (EzThrottler.Throttle("ActivityTargetPanel", 1000))
            {
                enemies = GetEnemies();
            }

            foreach (var enemy in enemies)
            {
                ImGui.TextUnformatted(enemy.Name.ToString());
                OcelotUi.Indent(() =>
                {
                    OcelotUi.LabelledValue(Ocelot.I18N.T("ui.object_kind"), enemy.ObjectKind);
                    OcelotUi.LabelledValue(Ocelot.I18N.T("ui.targetable"), enemy.IsTargetable ? Ocelot.I18N.T("ui.yes") : Ocelot.I18N.T("ui.no"));
                    OcelotUi.LabelledValue(Ocelot.I18N.T("ui.is_alive"), enemy.IsDead ? Ocelot.I18N.T("ui.no") : Ocelot.I18N.T("ui.yes"));
                    OcelotUi.LabelledValue(Ocelot.I18N.T("ui.is_activity_target"), IsActivityTarget(enemy, module) ? Ocelot.I18N.T("ui.yes") : Ocelot.I18N.T("ui.no"));
                });
            }
        });
    }

    private List<IGameObject> GetEnemies()
    {
        return Svc.Objects
            .Where(o => o != null && Player.DistanceTo(o) <= 50f && o.ObjectKind == ObjectKind.BattleNpc)
            .OrderBy(o => o.IsTargetable ? 0 : 1)
            .ToList();
    }

    private unsafe bool IsActivityTarget(IGameObject? obj, DebugModule module)
    {
        if (obj == null)
        {
            return false;
        }

        try
        {
            var battleChara = (BattleChara*)obj.Address;

            var id = battleChara->EventId.EntryId;
            var count = Svc.Data.GetExcelSheet<DynamicEvent>().Count();

            var activity = module.GetModule<AutomatorModule>().automator.Activity;
            if (activity != null)
            {
                var data = activity.data;
                if (data.Type == EventType.Fate)
                {
                    return battleChara->FateId == data!.Id;
                }
            }


            var isRelatedToCurrentEvent = battleChara->EventId.EntryId == Player.BattleChara->EventId.EntryId;

            return obj.SubKind == (byte)BattleNpcSubKind.Enemy && isRelatedToCurrentEvent;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex.Message);
            return false;
        }
    }
}
