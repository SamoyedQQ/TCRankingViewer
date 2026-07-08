using Dalamud.Game.Gui.ContextMenu;

namespace TCRankingViewer;

/// <summary>
/// 在遊戲原生右鍵選單（對玩家名字處）加入「查看 TC 排名履歷」。
/// 右鍵目標已直接帶名稱＋伺服器，查詢精準，且完全不需開角色卡（無閃現問題）。
/// </summary>
public sealed class PlayerContextMenu : IDisposable
{
    public PlayerContextMenu()
    {
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // 只處理「一般目標」選單（排除背包等 MenuTargetInventory）
        if (args.Target is not MenuTargetDefault target) return;

        var name = target.TargetName;
        if (string.IsNullOrEmpty(name)) return;
        if (!IsPlayerNameContext(args.AddonName)) return;

        // 伺服器：玩家有 home world、NPC 沒有 → 用來過濾非玩家目標
        var world = target.TargetHomeWorld.ValueNullable?.Name.ToString();

        // 對世界/角色本體右鍵（AddonName 為空）時，若拿不到 world 多半是 NPC → 略過
        if (string.IsNullOrEmpty(world) && string.IsNullOrEmpty(args.AddonName)) return;

        args.AddMenuItem(new MenuItem
        {
            // PrefixChar 標示來源，避免與原生項混淆
            PrefixChar = 'T',
            Name       = "查看 TC 排名履歷",
            OnClicked  = _ => Plugin.PlayerHistoryWindow.Open(name, world),
        });
    }

    // 允許加入選單的情境：對世界角色本體右鍵（AddonName 空，含「查看冒險者銘牌」那個選單），
    // 或在含玩家名字的 UI 視窗內右鍵。刻意用白名單，避免加到與玩家無關的選單。
    private static bool IsPlayerNameContext(string? addonName)
    {
        if (string.IsNullOrEmpty(addonName)) return true;   // 世界/角色本體右鍵
        return addonName is
            "PartyMemberList" or "_PartyList" or
            "LookingForGroup" or "LookingForGroupDetail" or
            "ChatLog" or "CharacterInspect" or
            "FriendList" or "SocialList" or "ContactList" or
            "FreeCompany" or "LinkShell" or "CrossWorldLinkshell" or
            "ContentMemberList" or "BlackList" or "BeginnerChatList";
    }

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
    }
}
