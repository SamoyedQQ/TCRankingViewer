using System;
using Ocelot;

namespace BOCCHI;

/// <summary>Language codes are canonical so saved settings and commands share one lookup.</summary>
public static class Localization
{
    public static readonly string[] Codes = ["zh-TW", "en", "jp", "fr", "de", "uwu"];
    public static readonly string[] Names = ["繁體中文（台灣）", "English", "日本語", "Français", "Deutsch", "uwu"];

    public static string CurrentLanguage { get; private set; } = "zh-TW";

    public static string? Normalize(string? code)
    {
        // Preserve the old fork's Chinese alias, while keeping a distinct zh-TW catalog.
        if (string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "zh-Hant", StringComparison.OrdinalIgnoreCase))
            return "zh-TW";

        foreach (var supported in Codes)
            if (string.Equals(code, supported, StringComparison.OrdinalIgnoreCase))
                return supported;

        return null;
    }

    public static void SetLanguage(string code)
    {
        CurrentLanguage = Normalize(code) ?? "zh-TW";
        I18N.SetLanguage(CurrentLanguage);
    }

    public static string State<T>(T value) where T : struct, Enum
        => I18N.T($"states.{typeof(T).Name}.{value}");

    // Sheet names are display-only; IPC commands and game object identifiers stay intact.
    public static string GameName(string sheet, uint rowId, string original)
    {
        if (CurrentLanguage != "zh-TW")
            return original;

        var key = $"game.{sheet}.{rowId}";
        var translated = I18N.T(key);
        return translated == key ? original : translated;
    }
}
