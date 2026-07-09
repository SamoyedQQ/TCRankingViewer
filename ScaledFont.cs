using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using ImGuiNET;

namespace TCRankingViewer;

/// <summary>
/// 依使用者設定的整體縮放倍率，提供「以實際字級重新柵格化」的清晰字型。
///
/// 取代先前的 <c>ImGui.SetWindowFontScale</c>：後者只是把既有字型圖集的點陣放大取樣，
/// 任何非 1.0 倍率都會模糊；改用 Dalamud 字型圖集在「目標像素尺寸」重建字型後，各倍率
/// 都清晰。職業 icon 尺寸沿用字級度量（GetFrameHeight），故會一併等比清晰縮放。
///
/// 字型重建為非同步且成本較高，因此僅在倍率改變時重建一次（見 <see cref="EnsureBuilt"/>）；
/// 重建期間沿用預設字型，完成後自動切換。倍率 1.0 直接用預設字型，不額外建置圖集。
/// </summary>
public sealed class ScaledFont : IDisposable
{
    // 「不需縮放／字型尚未就緒」時回傳的無操作 scope，維持呼叫端統一的 using 寫法
    private static readonly IDisposable NoOp = new NoOpScope();

    private IFontHandle? _handle;
    private float _builtScale;   // 目前 _handle 對應的倍率（用來判斷是否需重建）

    /// <summary>
    /// 於視窗 Draw 開頭呼叫，並以 <c>using</c> 包住整段繪製；離開時自動還原字型。
    /// 倍率 1.0（或字型尚在背景重建）時回傳無操作 scope，該幀沿用預設字型。
    /// </summary>
    public IDisposable Push()
    {
        // SetWindowFontScale 是「每視窗持續生效」的狀態，一旦設過就一直留著。本外掛改用實際字級
        // 重建字型後，視窗字型倍率必須固定為 1.0，否則會與 push 的字型「二次點陣放大」而模糊。
        // 每幀強制歸位，順帶清掉舊版殘留在各視窗的非 1.0 倍率（否則需重建視窗才會消失）。
        ImGui.SetWindowFontScale(1f);

        var scale = Plugin.Configuration.GetUiScale();
        if (Math.Abs(scale - 1.0f) < 0.005f) return NoOp;   // 1.0 用預設字型，免建置圖集

        EnsureBuilt(scale);
        return _handle is { Available: true } ? _handle.Push() : NoOp;
    }

    // 確保字型已依目前倍率建立；倍率改變時丟棄舊 handle，以新的像素尺寸重建。
    private void EnsureBuilt(float scale)
    {
        if (_handle != null && Math.Abs(_builtScale - scale) < 0.005f) return;

        _handle?.Dispose();
        _builtScale = scale;
        var sizePx = UiBuilder.DefaultFontSizePx * scale;
        _handle = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(sizePx)));
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private sealed class NoOpScope : IDisposable { public void Dispose() { } }
}
