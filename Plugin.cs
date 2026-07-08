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
    [PluginService] internal static IContextMenu           ContextMenu         { get; private set; } = null!;

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
    public static PlayerHistoryWindow  PlayerHistoryWindow  { get; private set; }
    public static PlayerContextMenu    PlayerContextMenu    { get; private set; }
#pragma warning restore CS8618

    private const string CommandName = "/tcrank";

    public readonly WindowSystem WindowSystem = new("TCRankingViewer");

    public Plugin()
    {
        Configuration    = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        RankingService   = new RankingService();
        BlacklistService = new BlacklistService();
        CidCache         = new CidCache();

        MainWindow          = new MainWindow();
        ConfigWindow        = new ConfigWindow();
        PartyFinderWindow   = new PartyFinderWindow();
        PlayerHistoryWindow = new PlayerHistoryWindow();
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PartyFinderWindow);
        WindowSystem.AddWindow(PlayerHistoryWindow);

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
        PlayerContextMenu    = new PlayerContextMenu();

        // 啟動時非同步下載排名資料（ContinueWith 在 Framework 主線程執行，確保 IPartyList 可正確讀取）
        _ = RankingService.RefreshAsync()
            .ContinueWith(_ => Framework.RunOnTick(() => PartyWatcher.RebuildResults()));

        // server 同步：僅在使用者明確開啟「載入插件時自動同步社群」時才啟動同步
        // 首次安裝預設為 false，避免在使用者尚未閱讀同步須知前就上傳資料
        if (Configuration.AutoSyncOnStartup)
            _ = Task.Run(SyncOnStartupAsync);

        Log.Information("[TCRanking] 插件已載入。指令：/tcrank");
    }

    public void Dispose()
    {
        PlayerContextMenu.Dispose();
        PartyFinderInspector.Dispose();
        CharaCardLookup.Dispose();
        BlacklistService.Dispose();
        CidCache.Dispose();
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ConfigWindow.Dispose();
        PartyFinderWindow.Dispose();
        PlayerHistoryWindow.Dispose();
        PartyWatcher.Dispose();
        RankingService.Dispose();
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
    }

    // ── 社群資料同步 ────────────────────────────────────────────────────────
    // 同步狀態供設定頁顯示（兩個欄位都僅由 TriggerSyncAsync 寫入，UI 端唯讀）
    public static bool    IsSyncing      { get; private set; }
    public static string? LastSyncMessage { get; private set; }

    // 允許同步同時最多一個,避免按鈕連點或啟動同步與手動觸發互相覆蓋寫入
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    // 啟動時呼叫(含 3 秒 delay 讓排名下載先啟動避免競爭)
    private static async Task SyncOnStartupAsync()
    {
        await Task.Delay(3000);
        await TriggerSyncAsync();
    }

    // 設定頁「立即同步社群資料」按鈕呼叫,以及啟動同步共用此入口
    public static async Task TriggerSyncAsync()
    {
        if (!await _syncLock.WaitAsync(0))
        {
            // 已在同步中,忽略重複觸發
            return;
        }
        try
        {
            IsSyncing      = true;
            LastSyncMessage = "正在同步...";

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
            LastSyncMessage = $"✓ 同步完成 {DateTime.Now:HH:mm:ss}";
            Log.Debug("[Sync] 資料同步完成");
        }
        catch (Exception ex)
        {
            LastSyncMessage = $"✗ 同步失敗:{ex.Message}";
            Log.Warning(ex, "[Sync] 資料同步失敗");
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
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
