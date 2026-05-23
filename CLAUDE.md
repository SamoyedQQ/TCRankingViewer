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
- **Tab 切換**：`SetInitialCategoryFilter` 設 `_pendingCategory`；`DrawCategoryTabs` 第一幀以 `ImGuiTabItemFlags.SetSelected` 強制切換，之後清除。此 SDK 無 `(string, flags)` 重載，改呼叫 `ImGuiNative.igBeginTabItem`
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
- Dalamud.NET.Sdk 14.0.1
- System.Security.Cryptography.ProtectedData 8.0.0（DPAPI 加密 license key）
- ECommons（`ECommons\` 目錄，git submodule 殘留，csproj 已排除編譯）
- ImGui.NET（Private=false）

## Release 流程
**工作流程**：改完程式碼後先 `dotnet build -c Release` 給使用者測試，**確認測試通過並獲得同意後**才執行 commit / push / release，不得提前發版。

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
