# BOCCHI 繁體中文（台灣）

本次修改以 `api13-tw` 分支的 `4ccbbd1` 為基礎，版本為 **1.0.1.2 / Dalamud API13**。

## 語言設定

首次使用與沒有語言欄位的既有設定，預設使用 `zh-TW`。語言設定會保存，可在設定視窗最上方切換，或使用 `/bocchi language zh-TW`。代碼不區分大小寫，舊別名 `zh` 與 `zh-Hant` 也會選擇繁體中文。移除愚人節隨機切換語言的行為。

繁中資源位於 `Translations/zh-TW`，不依賴 `DALAMUD_CN` 建置條件。既有 `Translations/zh` 不再作為此 fork 的繁中來源。

已處理主視窗、所有設定模組、全自動模式視窗、尋寶與胡蘿蔔搜尋、狀態文字、聊天通知、指令說明、偵錯視窗，以及 Ocelot 的缺少外掛、衝突、實驗功能與多選欄位提示。外掛品牌、作者名稱、指令、識別碼與偵錯資料值保留原始意義。

## 遊戲專有名詞

來源為 [thewakingsands/ffxiv-datamining-tc](https://github.com/thewakingsands/ffxiv-datamining-tc/tree/1cf1f9fb735bad0748848dbd3f4de05721fe619a)，固定參考版本 `1cf1f9fb735bad0748848dbd3f4de05721fe619a`。

- 依資料列 ID 核對 `Fate`、`DynamicEvent`、`Item`、`PlaceName`、`BNpcName`、`Mount`、`MKDSupportJob`，將此 fork 會用到的名稱收錄於 `game.json`。
- 參考 `Action`、`EObjName` 的技能與財寶箱名稱。
- 使用「幸福的魔法甕」、「輔助預言士」、「戰鬥之鈴」、「魔尋寶」、「亞返回」、「青銅財寶箱」、「白銀財寶箱」等資料表名稱。
- 六色半魂晶依 ID 對應為青、碧、綠、橙、紫、黃。
- 官方名稱中的「回廊」、「凶惡」維持資料表原文，未套用機械式詞彙轉換。

遊戲名稱的替換僅用於顯示。導航資料、物件 ID、IPC 與遊戲指令不變。未收錄的新資料列保留遊戲客戶端提供的名稱。

## 建置與驗證

Release 改為直接建置 Ocelot 子模組，以包含共用介面的翻譯。`tools/ocelot-zh-TW.patch` 保存本次對子模組的程式修改，不包含既有的套件鎖定檔變更。新複製的工作目錄請先初始化子模組並套用補丁；腳本可重複執行，遇到衝突會停止，不會覆蓋其他修改。

```powershell
git submodule update --init --recursive
powershell -File tools/apply-ocelot-localization.ps1
python -X utf8 tools/validate_localization.py
dotnet run --project tools/LocalizationSmoke -- .
dotnet build BOCCHI/BOCCHI.csproj -c Release -p:DalamudLibPath="C:/path/to/api13/dalamud/"
dotnet build BOCCHI/BOCCHI.csproj -c Debug -p:DalamudLibPath="C:/path/to/api13/dalamud/"
```

驗證包含英文鍵值的繁中覆蓋率、重複／空白鍵值、直接引用與自動產生的設定鍵值、格式參數、語言別名、切換事件、狀態名稱及遊戲名詞。執行測試直接使用 Ocelot 的實際 I18N 程式碼，沒有替換翻譯載入器。

本機通過 API13 Release、Debug 建置與翻譯測試。尚未在遊戲中逐頁操作驗證畫面、字型與實際導航功能。
