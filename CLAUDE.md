# TCRankingViewer — Claude Context

## 專案概述
Dalamud 插件，用於 FFXIV 繁中服 (TC)，在招募面板 (LookingForGroupDetail) 旁顯示隊員零式/絕本排名。
Repo：`https://github.com/SamoyedQQ/TCRankingViewer`（公開）

後端服務：`https://api.tc-ranking-viewer.workers.dev`（Cloudflare Worker，私有）
排名資料由 Worker 每小時爬取並快取，插件持 HMAC 簽名 license 向 Worker 取得資料。

## 編譯指令
```
dotnet build -c Release
```
輸出：`bin\Release\TCRankingViewer.dll`

> **API13（Dalamud.NET.Sdk 14.0.1 / net10.0 / Dalamud.Bindings.ImGui）**：本插件已升級至
> DalamudApiLevel 13，必須對「API13 的 Dalamud」編譯。SDK 預設抓 `Hooks\dev\`；若該資料夾
> 仍是舊的 API12 Dalamud（引用 ImGui.NET），編譯會失敗。XIVLauncher 下載的 API13 Dalamud
> 位於 `%AppData%\XIVLauncher\addon\Hooks\<版號>\`（例：`14.0.1.0`）。在 dev 尚未切到 API13 前，
> 可用環境變數覆寫：
> ```bash
> DALAMUD_HOME="$APPDATA/XIVLauncher/addon/Hooks/14.0.1.0" dotnet build -c Release
> ```

## 架構概覽

| 檔案 | 職責 |
|------|------|
| `Plugin.cs` | 入口點，持有所有靜態服務；啟動時執行 `SyncAsync` 同步社群資料 |
| `Configuration.cs` | 使用者設定，含 DPAPI 加密 license key、快取時間、通知開關、4 個社群同步開關 |
| `RankingService.cs` | 帶 HMAC Authorization header 向 Worker API 取得排名資料，建立玩家名稱索引；含 4 個社群同步方法 |
| `RankingModels.cs` | 資料模型（內部 `RankingEntry`、`BlacklistEntry`、Worker 回傳 JSON 反序列化模型）+ `JobAbbrev` + `EncounterMeta` |
| `PartyWatcher.cs` | 每幀監控隊伍成員變化，觸發排名查詢/聊天通知 |
| `PartyFinderInspector.cs` | 偵測 LookingForGroupDetail 開關、收集隊員名稱/職業/CID、查詢排名 |
| `CharaCardLookup.cs` | 用 AgentCharaCard hook 解析跨服玩家名稱，靜音 CharaCard 音效 |
| `CidCache.cs` | 持久化 ContentId → 玩家名稱快取；含 `GetAll()` / `MergeServerEntries()` 支援社群同步 |
| `BlacklistService.cs` | 本機黑名單（含備註）+ 伺服器端共享黑名單，兩者合併判斷 |

## License 認證機制

插件持有以 Windows DPAPI 加密的 UUID（綁定當前使用者帳號，不以明文存磁碟）。
每次 API 請求重新計算簽名，不重複使用 header：

```
id  = hex(SHA256(uuid))[:16]           ← 公開識別符，不含 UUID
ts  = Unix timestamp（秒）
sig = base64(HMAC-SHA256(key=uuid, data="{ts}|{path}"))
Authorization: HMAC {id}|{ts}|{sig}
```

Worker 以 `crypto.subtle.verify` 驗證、拒絕偏差 >±5 分鐘的時間戳（防重放）。

## Worker API 端點

| 端點 | 方法 | 說明 |
|------|------|------|
| `/encounters` | GET | Kantai235 副本清單 JSON |
| `/rankings/{key}` | GET | 指定副本排名 JSON |
| `/ultimate` | GET | SamoyedQQ 絕本排名 JSON |

所有端點均須有效的 `Authorization: HMAC ...` header。

| `/shared/cidcache` | GET | 下載伺服器端 CID→名稱快取（社群彙整）|
| `/shared/cidcache` | POST | 上傳本機 CID→名稱快取（貢獻） |
| `/shared/blacklist` | GET | 下載伺服器端共享黑名單 |
| `/shared/blacklist` | POST | 上傳本機黑名單（含備註）|
| `/admin` | GET | 瀏覽器管理後台（需 ADMIN_SECRET query param）|
| `/admin/data` | GET | 管理後台 JSON 資料 |

## RankingService 資料流

1. 並行：GET `/encounters` + GET `/ultimate`
2. 解析 encounters 取得副本 key 列表
3. 並行：GET `/rankings/{key}` × N
4. 合併兩來源 → `BuildIndex(List<RankingEntry>)` → `_index[name.ToLower()]`

### Kantai235 資料格式（零式/極神）

```json
// GET /encounters
{ "encounters": [{ "key": "m4s", "name": "零式 M4S", "category": "零式" }] }

// GET /rankings/{key}
{
  "encounter": { "key": "m4s", "name": "零式 M4S", "category": "零式" },
  "ranking_entries": [
    { "character_name": "玩家名", "job": "Dragoon", "rdps": 40000, "adps": 39000 }
  ]
}
```

`job_rank` 由 client-side 依 rDPS desc 排序後指派。

### SamoyedQQ 資料格式（絕本）

**`GET /ultimate`** — `Dictionary<string, SamoyedEntry>`

```json
{
  "玩家名@幻影群島:1077:Dragoon": {
    "name": "玩家名", "job": "Dragoon", "encounter": "The Omega Protocol",
    "encounter_id": 1077, "rdps": 38000, "is_clear": true,
    "duration_ms": 600000, "phase_reached": 0
  }
}
```

- 清板：`is_clear == true`；依 `(bossName, job)` 分組，同玩家保留最高 rDPS，rDPS desc 排序，client-side 指派 rank
- 未通關：`is_clear == false`；`phase_reached`（整數 1–N，直接來自 scraper）是相位的唯一可靠來源
- `encounter_id`（1073–1077）→ `MapSamoyedEncounterId()` → boss 中文名稱
- DSR（1076）有 7 個 phase：P1 亞德費爾 / P2 托爾丹 / P3 尼德霍格 / P4 左右眼 / P5 聖矛 / P6 宏斯瓦爾格 / P7 龍王托爾丹

## 社群資料同步

啟動時 `Plugin.SyncAsync()` 在背景執行（`Task.Delay(3000)` 讓排名下載先啟動），依設定選擇性執行：

```csharp
// 下載 → 合併（local wins）
if (Configuration.SyncCidCache)   CidCache.MergeServerEntries(await RankingService.DownloadSharedCidCacheAsync());
if (Configuration.SyncBlacklist)  BlacklistService.MergeServerEntries(await RankingService.DownloadSharedBlacklistAsync());
// 上傳
if (Configuration.UploadCidCache)   await RankingService.UploadCidCacheAsync(CidCache.GetAll());
if (Configuration.UploadBlacklist)  await RankingService.UploadBlacklistAsync(BlacklistService.GetAllLocalEntries());
```

**合併策略**：Local wins（本機已有的 key 不被伺服器覆蓋）。

**隱私原則**：CID 快取和黑名單均屬遊戲內公開可見資訊；設定頁顯示使用須知，四個開關均可獨立關閉。

### Configuration.cs — 新增設定

```csharp
public bool UploadCidCache  { get; set; } = true;  // 上傳本機 CID 快取
public bool SyncCidCache    { get; set; } = true;  // 下載伺服器 CID 快取
public bool UploadBlacklist { get; set; } = true;  // 上傳本機黑名單
public bool SyncBlacklist   { get; set; } = true;  // 下載伺服器黑名單
```

### BlacklistService.cs — 結構

以 `Dictionary<string, string>` 儲存（key = 名稱，value = 備註），區分本機和伺服器兩組：

```
_localEntries   // 來自本機 blacklist.txt
_serverEntries  // 來自伺服器同步（僅記憶體，不寫入 txt）
```

**黑名單檔案格式**（`blacklist.txt`）：
```
# 整行註解（# 開頭）
玩家名稱             ← 純名稱，備註為空
玩家名稱 # 備註文字  ← 名稱 + 備註（# 後空白可選）
```

- `IsBlacklisted(name)` — 同時檢查本機和伺服器
- `GetNote(name)` — **本機有備註時優先**；本機無備註（或不存在）才退回 `_serverNoteCache`；都沒有回傳 `null`
- `Count` — 本機條目數；`ServerCount` — 伺服器條目數（不含本機已有的）
- `GetAllLocalEntries()` — 回傳 `IEnumerable<BlacklistEntry>` 供上傳
- `MergeServerEntries(List<BlacklistEntry>)` — 不覆蓋本機已有名稱；同時建立 `_serverNoteCache`（含本機已有但 server 端有備註的條目，供 `GetNote` fallback 用）

#### 遊戲黑名單匯入（含備註）

`ReadAndMerge` 從 [`BlackListStringArray`](FFXIVClientStructs.FFXIV.Client.UI.Arrays) 讀取 `(PlayerNames, Notes)` 對齊 array — 這是 BlackList addon 顯示用的字串陣列，是**遊戲端唯一**能取得備註的來源（`InfoProxyBlacklist.BlockedCharacter` 只有 `Name/Id/Flag`，沒有備註欄位）。

`MergeFromGame` 採 read-modify-write，三類處理：
- 本機已有 name 且有備註 → 不動（本機永遠優先）
- 本機已有 name 但無備註、遊戲端有備註 → 原地補成 `name # note`
- 全新 name → append 至新時間區段（`# 遊戲黑名單匯入 yyyy-MM-dd HH:mm`），格式 `name # note` 或純 `name`

### RankingModels.cs — 新增模型

```csharp
public record BlacklistEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("note")] string Note);
```

## 關鍵設計

### PartyFinderInspector — 槽位順序
- `OnReceiveListing` 快取 `RawJobsPresent[]`（以 leaderCid 為 key），對應 UI 左→右槽位順序
- `_staged` 預先以槽位數填充空佔位，CharaCard 解析後以 `_staged[idx]` 原地更新
- **Bug fix**：遊戲有時會把 leader CID 放在 `MemberContentIds[0]`，後續成員 `jobSlotIdx = i+1` 偏移 1。修正：用 `leaderOccurrences` 計數，`i + 1 - leaderOccurrences` 作為 slot index

### CharaCardLookup — 靜音邏輯（state-based 設計）
Active state 由三個欄位管理：

| 欄位 | 職責 |
|------|------|
| `_pendingCid` | != 0 代表 RequestAsync 正在飛行中 |
| `_cleanupUntil` | 收尾期 tick；`RequestAsync.finally` 設 `now+800ms`；addon 出現時延長 `now+500ms` |
| `_hardCutoff` | 絕對安全網（`now+8000ms`）；保證最終 unmute |

`IsActive()` = `_pendingCid != 0 || _cleanupUntil > now`，作為靜音/addon 監控/toast 抑制的單一判斷依據。

### PartyFinderWindow
- `PreDraw`：每幀把視窗位置釘在 LookingForGroupDetail 右側
- **Tab 切換**：`SetInitialCategoryFilter` 設 `_pendingCategory`；`DrawCategoryTabs` 第一幀以 `ImGuiTabItemFlags.SetSelected` 強制切換，之後清除。API13 的 `Dalamud.Bindings.ImGui` 提供 `ImGui.BeginTabItem(label, flags)` 重載（內部即以 null p_open 呼叫），故直接使用，不再手動呼叫 native
- **摺疊/展開**：摺疊時優先顯示與當前副本 boss 相符的條目（`EncounterMeta.GetBossFragmentForDutyName`）；展開時回到 `SortByJobThenContent` 順序
- **TC 零式名稱**：`ContentFinderCondition.Name` 格式為「阿卡狄亞零式登天門技場 輕量級N」，以 `"輕量級N"` 比對並回傳對應 "MNS" fragment

### MainWindow / PartyFinderWindow — 黑名單按鈕

黑名單玩家不再顯示靜態紅字，改為 `DrawBlacklistButton(characterName)` 靜態方法：

```csharp
var popupId = $"blnote_{characterName}";
// 透明背景 SmallButton 顯示紅色「黑名單Ｘ」
if (ImGui.SmallButton($"黑名單Ｘ##{popupId}")) ImGui.OpenPopup(popupId);
// Popup：玩家名稱（紅）+ 分隔線 + 備註（灰色「無備註」）
if (ImGui.BeginPopup(popupId)) { ... ImGui.EndPopup(); }
```

**注意**：
- 此 popup 必須在 `ImGui.TableNextRow()` 範圍外的同一 ImGui frame 處理，否則 popup 會被 table clip 截斷。目前放置在 name 列後、BeginTable loop 內，ImGui 會正確渲染。
- 按鈕文字不可用單一 Unicode 符號（例如 `✕`），遊戲字型常會 fallback 成「=」之類的顯示。改用中文字「黑名單Ｘ」。

### MainWindow ↔ PartyFinderWindow 同步原則

兩個視窗的成員列各自有一份 `DrawBlacklistButton`、`DrawEntryColumns`、`RankColor`、職業色等實作（**目前是複製貼上、未抽共用**）。任何改動只在一邊修都會造成行為不一致（例如歷史 bug：popup 按鈕文字只改 MainWindow，PartyFinderWindow 留下 `✕` 顯示成「=」）。

**規範**：
- 修 `MainWindow.cs` 或 `PartyFinderWindow.cs` 任何屬於兩邊重複的區塊時，**主動詢問使用者是否同步另一個**，並指出對應位置與差異。
- 不要擅自抽 helper / 共用 class — 需先取得同意。

### MainWindow
- 展開時用 `EncounterMeta.SortByJobThenContent()` 排序，同職業條目集中顯示
- 各類別區塊內也用 `SortByJobThenContent`（`OrderBy(priority).ThenBy(rank)` 會跨職業交錯）

## 依賴
- Dalamud.NET.Sdk 14.0.1（API13，net10.0-windows）
- System.Security.Cryptography.ProtectedData 8.0.0（DPAPI 加密 license key）
- System.Drawing.Common 10.0.0（截圖 GDI；`ExcludeAssets=runtime`，執行期沿用框架版本）
- ECommons（`ECommons\` 目錄，git submodule 殘留，csproj 已排除編譯）
- ImGui：由 SDK 自動引入 `Dalamud.Bindings.ImGui`（`using Dalamud.Bindings.ImGui;`）。
  API13 已淘汰 ImGui.NET，不再手動 `<Reference Include="ImGui.NET">`。

## API13 遷移重點（2026-07）
API12 → API13 的破壞性變更與對應修正：
- `using ImGuiNET;` → `using Dalamud.Bindings.ImGui;`（全部 UI 檔）
- 貼圖 handle：`IDalamudTextureWrap.ImGuiHandle`（nint）→ `.Handle`（`ImTextureID`）；
  `ImGui.Image` 首參改吃 `ImTextureID`；空值判斷改 `ImTextureID.IsNull`（見 `RankCells.cs`）
- `IGameGui.GetAddonByName` 回傳 `AtkUnitBasePtr` 包裝結構（非 nint）；取原始指標用 `.Address`
  （`(AtkUnitBase*)addonPtr.Address`，見 `PartyFinderWindow.cs`）
- `ImGuiNative.igBeginTabItem` 移除 → 改用 `ImGui.BeginTabItem(label, flags)` 重載
- FFXIVClientStructs `InfoProxyCrossRealm.IsInCrossRealmParty` 由整數改 `bool`（去掉 `!= 0`）
- `IClientState.LocalPlayer` 標記 obsolete → 改用 `IObjectTable.LocalPlayer`（新增注入 `IObjectTable`）
- `System.Drawing.Common` 9.0.0 → 10.0.0（配合 net10 執行期，消除 MSB3277 版本衝突警告）

## Release 流程
**工作流程**：改完程式碼後先 `dotnet build -c Release` 給使用者測試，**確認測試通過並獲得同意後**才執行 commit / push / release，不得提前發版。

### 公開 release notes 撰寫規範
公開 repo（`https://github.com/SamoyedQQ/TCRankingViewer`）的 release notes 面向一般插件使用者，必須遵守以下規範：

- **禁止提及**後端實作細節：Worker、KV、D1、admin 後台、HMAC、dedup、Race condition、class 名稱（如 `RecordMeta`、`GetAllLocalEntries`）
- **用語**以使用者視角描述現象與結果，例如「伺服器與角色 ID 有時未能正確同步」而非「RecordMeta 過早過濾」
- **每則都要有「大眾看得懂的簡單版」**：用最白話講使用者實際感受到的好處（更快 / 更省流量 / 更穩定），不出現任何技術名詞。純後端改動也要翻成使用者視角的一句話（例：「後端大幅優化，服務更穩定、能支撐更多人同時使用」），不可省略、也不可直接寫技術細節。
- **詳細技術說明**（成因分析、架構決策、Worker 端改動）只寫在私有 repo（`TCRankingViewerOLD`）的 release notes

每次發版需：
1. 更新 `TCRankingViewer.csproj` 的 `<Version>`、`TCRankingViewer.json` 的 `AssemblyVersion`、**`repo.json` 的 `AssemblyVersion`**（三處同步）
2. `dotnet build -c Release`
3. 打包插件 ZIP：
   ```
   Compress-Archive -Path "bin\Release\TCRankingViewer.dll","bin\Release\TCRankingViewer.json","bin\Release\icon.png" -DestinationPath "bin\Release\TCRankingViewer.zip" -Force
   ```
4. 發版到主 repo：
   ```
   gh release create vX.X.X.X --title "vX.X.X.X" --notes "..." "bin\Release\TCRankingViewer.zip"
   ```
   `repo.json` 已在主 repo 中，push 後自動生效，無需額外步驟。

## 注意事項
- 所有 UI 操作（ImGui）必須在 framework thread
- `CharaCardLookup.RequestAsync` 有 semaphore（同一時間只查一個）+ 200ms throttle
- 插件指令：`/tcrank`（主視窗）、`/tcrank config`（設定）
- **刪除或移植任何原本功能前，務必先備份原始程式碼，以免改壞需要還原**
- 插件端**不得**加入任何操控 Worker 的程式碼（如觸發爬蟲、管理 license）；Worker 管理一律從本機 CLI 執行
