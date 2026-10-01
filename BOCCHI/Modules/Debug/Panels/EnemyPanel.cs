using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class EnemyPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.nearby_enemies");
    }

    private List<IGameObject> enemies = [];

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            foreach (var enemy in enemies)
            {
                if (ImGui.CollapsingHeader($"{enemy.Name} - {enemy.DataId}##{enemy.ObjectIndex}"))
                {
                    OcelotUi.Indent(() =>
                    {
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_name") + $": {enemy.Name.TextValue}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_gameobjectid") + $": {enemy.GameObjectId:X}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_entityid") + $": {enemy.EntityId:X}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_dataid") + $": {enemy.DataId}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_ownerid") + $": {enemy.OwnerId}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_objectindex") + $": {enemy.ObjectIndex}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_objectkind") + $": {enemy.ObjectKind}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_subkind") + $": {enemy.SubKind}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_position") + $": {enemy.Position}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_rotation") + $": {enemy.Rotation}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_hitboxradius") + $": {enemy.HitboxRadius}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_yalmdistancex") + $": {enemy.YalmDistanceX}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_yalmdistancez") + $": {enemy.YalmDistanceZ}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_isdead") + $": {(enemy.IsDead ? Ocelot.I18N.T("ui.yes") : Ocelot.I18N.T("ui.no"))}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_istargetable") + $": {(enemy.IsTargetable ? Ocelot.I18N.T("ui.yes") : Ocelot.I18N.T("ui.no"))}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_targetobjectid") + $": {enemy.TargetObjectId:X}");

                        if (enemy.TargetObject is { } target)
                        {
                            ImGui.Text(Ocelot.I18N.T("ui.enemy_targetobject") + $": {target.Name.TextValue} ({target.GameObjectId:X})");
                        }
                        else
                        {
                            ImGui.Text(Ocelot.I18N.T("ui.enemy_targetobject") + ": " + Ocelot.I18N.T("ui.none"));
                        }

                        ImGui.Text(Ocelot.I18N.T("ui.enemy_isvalid") + $": {(enemy.IsValid() ? Ocelot.I18N.T("ui.yes") : Ocelot.I18N.T("ui.no"))}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_address") + $": 0x{enemy.Address.ToInt64():X}");


                        var battleChara = (BattleChara*)enemy.Address;


                        ImGui.Text(Ocelot.I18N.T("ui.enemy_layoutid") + $": {battleChara->LayoutId}");
                        ImGui.Text(Ocelot.I18N.T("ui.enemy_level") + $": {battleChara->ForayInfo.Level}");

                        var distance = Player.DistanceTo(enemy.Position);
                        if (distance <= 30f)
                        {
                            if (ImGui.Button(Ocelot.I18N.T("ui.target")))
                            {
                                Svc.Targets.Target = enemy;
                            }
                        }
                    });
                }
            }
        });
    }

    public override void Update(DebugModule module)
    {
        if (EzThrottler.Throttle("enemies", 2000))
        {
            // DoThing();
            enemies = Svc.Objects
                .Where(o =>
                    o != null &&
                    o.IsHostile() &&
                    o.IsTargetable &&
                    o.Name.TextValue.Length > 0
                )
                .OrderBy(o => Vector3.Distance(o.Position, Player.Position))
                .ToList();
        }
    }
}
