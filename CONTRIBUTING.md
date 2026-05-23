# TCRankingViewer — 開發者文件

本文件供協作開發者參考，涵蓋環境建置、架構設計、關鍵實作細節與發版流程。
使用者教學請見 [README.md](README.md)。

---

## 開發環境需求

| 工具 | 版本 | 備註 |
|------|------|------|
| .NET SDK | 9.0+ | `dotnet --version` 確認 |
| Dalamud.NET.Sdk | 14.0.1 | 由 csproj 自動還原 |
| FFXIV 繁中服 (TC) | 最新 | 搭配 XIVLauncher + Dalamud |
| Visual Studio / Rider | 任意 | 或 `dotnet build` CLI |
| GitHub CLI (`gh`) | 任意 | 發版用，非必要 |

### 首次設定

```powershell
# 還原依賴（Dalamud.NET.Sdk 會自動下載 Dalamud 參考組件）
dotnet restore

# 建置 Release
dotnet build -c Release
```

> **注意**：`ECommons\` 目錄是 git submodule 殘留，已在 csproj 中排除編譯，不影響建置。

---

## 專案結構

```
TCRankingViewer/
├── Plugin.cs                  # 入口點，持有所有靜態服務，負責初始化與 Dispose
├── Configuration.cs           # 使用者設定（DPAPI 加密 license key、快取時間、通知開關）
│
├── RankingModels.cs           # 所有資料模型 + EncounterMeta + JobAbbrev
├── RankingService.cs          # 帶 HMAC 認證取得排名資料，建立玩家名稱索引
│
├── PartyWatcher.cs            # 監控隊伍成員（IPartyList），觸發查詢/聊天通知
├── PartyFinderInspector.cs    # 偵測招募面板開關，收集隊員名稱/職業/CID
├── CharaCardLookup.cs         # AgentCharaCard hook，解析跨服玩家名稱
├── CidCache.cs                # 持久化 ContentId → 玩家名稱快取（JSON 檔案）
│
├── MainWindow.cs              # 主視窗（/tcrank）— 隊伍排名表格
├── PartyFinderWindow.cs       # 招募面板側邊視窗 — 自動貼附 LookingForGroupDetail 右側
├── ConfigWindow.cs            # 設定視窗（/tcrank config）
├── BlacklistService.cs        # 黑名單讀取與查詢
│
├── TCRankingViewer.csproj     # 專案定義，版本號在此更新
└── TCRankingViewer.json       # Dalamud 插件元資料，AssemblyVersion 需與 csproj 同步
```

---

## 架構說明

### 服務依賴圖

```
Plugin（靜態持有者）
  ├── RankingService          ← 獨立，向後端 Worker 取得資料
  ├── BlacklistService        ← 獨立，讀取黑名單
  ├── CidCache                ← 獨立，持久化 CID 快取
  ├── CharaCardLookup         ← 需要 GameInteropProvider（hook）
  ├── PartyWatcher            ← 需要 RankingService、Framework
  └── PartyFinderInspector    ← 需要 CharaCardLookup、RankingService、PartyFinderWindow
```

所有服務透過 `Plugin.Xxx`（靜態）存取，不使用 DI 容器傳遞。

### 資料流

```
啟動
  └─ RankingService.RefreshAsync()
       ├─ 讀取 Configuration.GetLicenseKey()
       ├─ POST /encounters   (帶 HMAC Authorization header)
       ├─ GET  /ultimate     (帶 HMAC Authorization header)
       └─ GET  /rankings/{key} × N  (並行，帶 HMAC Authorization header)
            └─ BuildIndex() → _index[name.ToLower()]

進入隊伍（PartyWatcher）
  └─ 每幀偵測 IPartyList 變化
       └─ RebuildResults() → 查 _index → 顯示於 MainWindow

開啟招募面板（PartyFinderInspector）
  └─ OnLookingForGroupDetailOpen
       ├─ 解析 AgentLookingForGroup.MemberContentIds
       ├─ 已知 CID → CidCache 取名
       └─ 未知 CID → CharaCardLookup.RequestAsync（依序，含 throttle）
            └─ 結果填入 _staged[idx] → PartyFinderWindow 更新顯示
```

### HMAC 認證流程

插件不以明文傳輸 license key，而是以其作為 HMAC 簽名金鑰：

```
license_uuid（DPAPI 解密後只存記憶體）
  │
  ├─ id  = hex(SHA256(uuid))[:16]        ← 公開識別符
  ├─ ts  = Unix timestamp（秒）
  └─ sig = base64(HMAC-SHA256(key=uuid, data="{ts}|{path}"))
       │
       └─ Authorization: HMAC {id}|{ts}|{sig}
```

後端 Worker 驗證簽名並拒絕時間偏差超過 ±5 分鐘的請求（防重放攻擊）。

---

## 關鍵設計細節

### PartyFinderInspector — 槽位對應（`CollectMemberSlots`）

遊戲的 `AgentLookingForGroupListing.MemberContentIds[]` 對應招募 UI 的左→右槽位，
**但有時** 遊戲會把 leader 自己的 CID 額外塞在 index 0，造成後續成員槽位偏移。

修正方式：用 `leaderOccurrences` 計數跳過的 leader CID：

```csharp
var leaderOccurrences = 0;
for (var i = 0; i < memberIds.Length; i++)
{
    var cid = memberIds[i];
    if (cid == 0) continue;
    if (cid == leaderCid) { leaderOccurrences++; continue; }
    var jobSlotIdx = i + 1 - leaderOccurrences;  // 修正前：i + 1（偏移 bug）
    // ...
}
```

### CharaCardLookup — 靜音邏輯

CharaCard 開啟時遊戲會播音效。插件自動靜音，由三個欄位管理狀態：

| 欄位 | 職責 |
|------|------|
| `_pendingCid` | != 0 代表 RequestAsync 正在飛行中 |
| `_cleanupUntil` | 收尾期 tick；`RequestAsync.finally` 設 `now+800ms`；addon 出現時延長 `now+500ms` |
| `_hardCutoff` | 絕對安全網（`now+8000ms`）；保證最終 unmute |

`IsActive()` = `_pendingCid != 0 || _cleanupUntil > now`，是靜音 / addon 監控 / toast 抑制的單一判斷依據。

### PartyFinderWindow — 標籤頁強制切換

ImGui tab bar 以 tab bar ID 維護內部 active-tab 狀態；即使邏輯上切換了 `_categoryFilter`，
下一幀 ImGui 仍會回傳第一個 tab 為 active。

解法：`_pendingCategory` + `ImGuiTabItemFlags.SetSelected`，只在被設定後的**第一幀**觸發，之後清除。

**ImGui.NET 版本限制**：此 SDK 的 `ImGui.BeginTabItem` 只有 `(string)` 和 `(string, ref bool, flags)` 兩個重載。
`ref bool` 版本會顯示關閉按鈕（不想要）。因此改呼叫 native API：

```csharp
private static bool TabItem(string label, bool forceSelect)
{
    var flags = forceSelect ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
    var byteCount = System.Text.Encoding.UTF8.GetByteCount(label);
    byte* pLabel = stackalloc byte[byteCount + 1];
    System.Text.Encoding.UTF8.GetBytes(label, new Span<byte>(pLabel, byteCount));
    pLabel[byteCount] = 0;
    return ImGuiNative.igBeginTabItem(pLabel, null, flags) != 0;
}
```

### MainWindow ↔ PartyFinderWindow 同步原則

`MainWindow.cs` 與 `PartyFinderWindow.cs` 內部各自有一份 `DrawBlacklistButton`、`DrawEntryColumns`、`RankColor`、職業顏色表等實作，**目前為複製貼上、未抽共用 helper**。歷史上曾因為只改一邊（例如 popup 按鈕文字 `✕` → `黑名單Ｘ` 只更新 MainWindow）造成兩個視窗顯示不一致。

**規範**：
- 修改 `MainWindow.cs` 或 `PartyFinderWindow.cs` 中屬於兩邊共有的區塊時，**主動詢問使用者是否同步另一個檔案**，並指明對應位置。
- 不要在未取得同意前擅自抽 helper / 共用 class。

### BlacklistService — 遊戲黑名單匯入（含備註）

遊戲端唯一可取得備註的資料來源是 [`BlackListStringArray`](FFXIVClientStructs.FFXIV.Client.UI.Arrays)（BlackList addon 顯示用的 UI string array），包含 `PlayerNames` / `Homeworlds` / `Notes` 三組 200-element array，同 index 對齊。

`InfoProxyBlacklist.BlockedCharacter` 只有 `Name` / `Id` / `Flag`，**沒有備註欄位**，僅作為 fallback。

`MergeFromGame` 採 read-modify-write，三類處理：
1. 本機已有備註 → 不動（本機永遠優先）
2. 本機有名字但無備註、遊戲端有備註 → 原地補成 `name # note`
3. 全新名字 → append 至新的時間區段（含備註）

### EncounterMeta.SortByJobThenContent

展開多筆排名時，以**職業分組**排序（同職業條目集中顯示），各職業組再依最佳副本優先級排序：

```
龍騎 M4S #1 → 龍騎 M3S #2
武士 M4S #3 → 武士 M2S #5
```

若直接用 `OrderBy(priority).ThenBy(rank)` 會交錯不同職業，應一律改用 `SortByJobThenContent()`。

---

## 編譯

```powershell
dotnet build -c Release
# 輸出：bin\Release\TCRankingViewer.dll
```

---

## 發版流程

**原則**：改完程式碼後先 `dotnet build -c Release` 給使用者測試，**確認測試通過並獲得同意後**才執行 commit / push / release。

1. 更新版本號（三個檔案必須同步）：
   - `TCRankingViewer.csproj` → `<Version>X.X.X.X</Version>`
   - `TCRankingViewer.json` → `"AssemblyVersion": "X.X.X.X"`
   - `repo.json` → `"AssemblyVersion": "X.X.X.X"`

2. 建置：
   ```powershell
   dotnet build -c Release
   ```

3. 打包插件 ZIP：
   ```powershell
   Compress-Archive -Path "bin\Release\TCRankingViewer.dll","bin\Release\TCRankingViewer.json","bin\Release\icon.png" `
       -DestinationPath "bin\Release\TCRankingViewer.zip" -Force
   ```

4. 建立 GitHub Release：
   ```powershell
   gh release create vX.X.X.X `
       --title "vX.X.X.X" `
       --notes "..." `
       "bin\Release\TCRankingViewer.zip"
   ```

5. `repo.json` 已在主 repo 中，push 後自動生效，Dalamud 即可偵測到新版本。

---

## 注意事項

- **所有 ImGui 操作必須在 framework thread**（Dalamud UI Draw callback 或 `Framework.RunOnTick`）
- `CharaCardLookup.RequestAsync` 有 semaphore（同時只查一個）+ 200ms throttle
- `Plugin.cs` 中 `#pragma warning disable CS8618` 是因為 Dalamud 以反射注入 `[PluginService]`，屬已知模式
- ECommons 目錄已在 csproj 中以 `Remove` 排除，不參與編譯，可安全忽略
- **刪除或移植任何原本功能前，務必先備份原始程式碼，以免改壞需要還原**

---

## Dalamud API 參考

- [Dalamud 開發文件](https://dalamud.dev/)
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
- [Dalamud.NET.Sdk NuGet](https://www.nuget.org/packages/Dalamud.NET.Sdk)
