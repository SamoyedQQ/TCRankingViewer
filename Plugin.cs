using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Hooking;

namespace TCRankingViewer;

public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud Services ────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface  { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager    { get; private set; } = null!;
    [PluginService] internal static IPartyList             PartyList         { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState       { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui           { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework         { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log               { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle        AddonLifecycle    { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui           { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui        PartyFinderGui    { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IToastGui              ToastGui            { get; private set; } = null!;
    [PluginService] internal static IGameConfig            GameConfig          { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager         { get; private set; } = null!;

    // ── Plugin-owned Services ───────────────────────────────────────────────
#pragma warning disable CS8618
    public static Configuration        Configuration        { get; private set; }
    public static RankingService       RankingService       { get; private set; }
    public static BlacklistService     BlacklistService     { get; private set; }
    public static CidCache             CidCache             { get; private set; }
    public static PartyWatcher         PartyWatcher         { get; private set; }
    public static CharaCardLookup       CharaCardLookup      { get; private set; }
    public static PartyFinderInspector PartyFinderInspector { get; private set; }
    public static MainWindow           MainWindow           { get; private set; }
    public static ConfigWindow         ConfigWindow         { get; private set; }
    public static PartyFinderWindow    PartyFinderWindow    { get; private set; }
#pragma warning restore CS8618

    private const string CommandName = "/tcrank";

    public readonly WindowSystem WindowSystem = new("TCRankingViewer");

    public Plugin()
    {
        Configuration    = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        RankingService   = new RankingService();
        BlacklistService = new BlacklistService();
        CidCache         = new CidCache();

        MainWindow        = new MainWindow();
        ConfigWindow      = new ConfigWindow();
        PartyFinderWindow = new PartyFinderWindow();
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PartyFinderWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 TC 繁中服 Savage 排名視窗（/tcrank config 開啟設定）"
        });

        PluginInterface.UiBuilder.Draw        += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi  += ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        CharaCardLookup      = new CharaCardLookup();
        PartyWatcher         = new PartyWatcher();
        PartyFinderInspector = new PartyFinderInspector();

        // 啟動時非同步下載排名資料（ContinueWith 在 Framework 主線程執行，確保 IPartyList 可正確讀取）
        _ = RankingService.RefreshAsync()
            .ContinueWith(_ => Framework.RunOnTick(() => PartyWatcher.RebuildResults()));

        // server 同步：等待排名資料先下載完成後再執行，避免競爭
        _ = Task.Run(SyncAsync);

        Log.Information("[TCRanking] 插件已載入。指令：/tcrank");
    }

    public void Dispose()
    {
        PartyFinderInspector.Dispose();
        CharaCardLookup.Dispose();
        BlacklistService.Dispose();
        CidCache.Dispose();
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ConfigWindow.Dispose();
        PartyFinderWindow.Dispose();
        PartyWatcher.Dispose();
        RankingService.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
    }

    // ── 啟動時 server 資料同步（非阻塞，失敗不影響主功能）────────────────────
    private static async Task SyncAsync()
    {
        // 稍作等待，讓排名下載先啟動
        await Task.Delay(3000);
        try
        {
            var tasks = new List<Task>();

            if (Configuration.SyncCidCache)
                tasks.Add(RankingService.DownloadSharedCidCacheAsync()
                    .ContinueWith(t => { if (!t.IsFaulted && t.Result.Count > 0)
                        CidCache.MergeServerEntries(t.Result); }));

            if (Configuration.UploadCidCache)
                tasks.Add(RankingService.UploadCidCacheAsync(CidCache.GetAll()));

            if (Configuration.SyncBlacklist)
                tasks.Add(RankingService.DownloadSharedBlacklistAsync()
                    .ContinueWith(t => { if (!t.IsFaulted && t.Result.Count > 0)
                        BlacklistService.MergeServerEntries(t.Result); }));

            if (Configuration.UploadBlacklist)
                tasks.Add(RankingService.UploadBlacklistAsync(BlacklistService.GetAllLocalEntries()));

            await Task.WhenAll(tasks);
            Log.Debug("[Sync] 資料同步完成");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sync] 資料同步失敗");
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            ConfigWindow.Toggle();
        else
            MainWindow.Toggle();
    }

    private void DrawUI()        => WindowSystem.Draw();
    private void ToggleMainUI()  => MainWindow.Toggle();
    private void ToggleConfigUI() => ConfigWindow.Toggle();
}
