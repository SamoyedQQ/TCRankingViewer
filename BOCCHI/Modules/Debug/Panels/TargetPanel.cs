using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Dalamud.Bindings.ImGui;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class TargetPanel : Panel
{
    public override string GetName()
    {
        return Ocelot.I18N.T("ui.target");
    }

    public override unsafe void Render(DebugModule module)
    {
        OcelotUi.Indent(() =>
        {
            var target = Svc.Targets.Target;
            if (target == null)
            {
                ImGui.TextUnformatted(Ocelot.I18N.T("ui.no_target_selected"));
                return;
            }

            // Try to cast to internal GameObject
            var obj = (GameObject*)target.Address;

            if (obj == null)
            {
                ImGui.TextUnformatted(Ocelot.I18N.T("ui.target_is_not_a_native_gameobject"));
                return;
            }

            void Draw<T>(string label, T value)
            {
                OcelotUi.Title($"{label}:");
                ImGui.SameLine();
                ImGui.TextUnformatted(value?.ToString() ?? Ocelot.I18N.T("ui.null"));
            }

            Draw(Ocelot.I18N.T("ui.name_2"), obj->NameString);
            Draw(Ocelot.I18N.T("ui.eventstate"), obj->EventState);
            Draw(Ocelot.I18N.T("ui.entityid"), obj->EntityId);
            Draw(Ocelot.I18N.T("ui.layoutid"), obj->LayoutId);
            Draw(Ocelot.I18N.T("ui.baseid"), obj->BaseId);
            Draw(Ocelot.I18N.T("ui.ownerid"), obj->OwnerId);
            Draw(Ocelot.I18N.T("ui.objectindex"), obj->ObjectIndex);
            Draw(Ocelot.I18N.T("ui.objectkind"), obj->ObjectKind);
            Draw(Ocelot.I18N.T("ui.subkind"), obj->SubKind);
            Draw(Ocelot.I18N.T("ui.sex"), obj->Sex);
            Draw(Ocelot.I18N.T("ui.yalmdistx"), obj->YalmDistanceFromPlayerX);
            Draw(Ocelot.I18N.T("ui.targetstatus"), obj->TargetStatus);
            Draw(Ocelot.I18N.T("ui.yalmdistz"), obj->YalmDistanceFromPlayerZ);
            Draw(Ocelot.I18N.T("ui.targetablestatus"), obj->TargetableStatus);
            Draw(Ocelot.I18N.T("ui.position"), obj->Position);
            Draw(Ocelot.I18N.T("ui.rotation"), obj->Rotation);
            Draw(Ocelot.I18N.T("ui.scale"), obj->Scale);
            Draw(Ocelot.I18N.T("ui.height"), obj->Height);
            Draw(Ocelot.I18N.T("ui.vfxscale"), obj->VfxScale);
            Draw(Ocelot.I18N.T("ui.hitboxradius"), obj->HitboxRadius);
            Draw(Ocelot.I18N.T("ui.drawoffset"), obj->DrawOffset);
            Draw(Ocelot.I18N.T("ui.eventid"), obj->EventId);
            Draw(Ocelot.I18N.T("ui.fateid"), obj->FateId);
            Draw(Ocelot.I18N.T("ui.nameplateiconid"), obj->NamePlateIconId);
            Draw(Ocelot.I18N.T("ui.renderflags"), obj->RenderFlags);

            // Pointers and advanced types
            Draw(Ocelot.I18N.T("ui.drawobject"), (ulong)obj->DrawObject);
            Draw(Ocelot.I18N.T("ui.sharedgrouplayoutinstance"), (ulong)obj->SharedGroupLayoutInstance);
            Draw(Ocelot.I18N.T("ui.luaactor"), (ulong)obj->LuaActor);
            Draw(Ocelot.I18N.T("ui.eventhandler"), (ulong)obj->EventHandler);

            // Virtual methods (callable via vtable)
            Draw(Ocelot.I18N.T("ui.istargetable"), obj->GetIsTargetable());
            Draw(Ocelot.I18N.T("ui.radius_3"), obj->GetRadius());
            Draw(Ocelot.I18N.T("ui.height_virtual"), obj->GetHeight());
            Draw(Ocelot.I18N.T("ui.sex_virtual"), obj->GetSex());
            Draw(Ocelot.I18N.T("ui.isdead"), obj->IsDead());
            Draw(Ocelot.I18N.T("ui.isnotmounted"), obj->IsNotMounted());
            Draw(Ocelot.I18N.T("ui.ischaracter"), obj->IsCharacter());
        });
    }


    // public override void Update(DebugModule module)
    // {
    //     if (EzThrottler.Throttle("enemies", 2000))
    //     {
    //         // DoThing();
    //         enemies = Svc.Objects
    //             .Where(o =>
    //                 o != null &&
    //                 o.IsHostile() &&
    //                 o.IsTargetable &&
    //                 o.Name.TextValue.Length > 0
    //             )
    //             .OrderBy(o => Vector3.Distance(o.Position, Player.Position))
    //             .ToList();
    //     }
    // }
}
