using BOCCHI;
using BOCCHI.Modules.MobFarmer.States;
using BOCCHI.Modules.StateManager;
using Ocelot;
using System.Text;
using System.Text.Json.Nodes;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
I18N.SetDirectory(root);
foreach (var lang in new[] { "en", "zh-TW" })
    I18N.LoadAllFromDirectory(lang, $"Translations/{lang}");

foreach (var alias in new[] { "zh-TW", "zh-tw", "ZH-TW", "zh", "zh-Hant" })
{
    Check(Localization.Normalize(alias) == "zh-TW", $"Invalid normalization: {alias}");
    Localization.SetLanguage(alias);
    Check(I18N.T("generic.label.start") == "開始", $"Alias not loaded: {alias}");
}
Check(Localization.Normalize("invalid") == null, "Unsupported code accepted");
Check(Localization.Normalize(null) == null, "Null code accepted");
Check(Localization.State(State.InCombat) == "戰鬥中", "Untranslated state");
Check(Localization.State(FarmerPhase.Gathering) == "引怪中", "Untranslated phase");
Check(Localization.GameName("Fate", 1976, "Persistent Pots") == "幸福的魔法甕", "Official FATE name");
Check(Localization.GameName("Item", 47747, "Realgar Demiatma") == "橙色半魂晶", "Official item name");
Check(Localization.GameName("PlaceName", 4944, "Base Camp") == "調查隊營地", "Official aethernet name");

var changes = 0;
I18N.OnLanguageChanged += (_, _) => changes++;
Localization.SetLanguage("en");
Check(I18N.T("generic.label.start") == "Start", "English switching failed");
Check(Localization.GameName("Fate", 1976, "Persistent Pots") == "Persistent Pots", "Changed English game name");
Localization.SetLanguage("zh-TW");
Check(changes == 2, "Window language update events missing");

static IEnumerable<(string Key, string Value)> Flatten(JsonObject obj, string prefix = "")
{
    foreach (var (name, value) in obj)
    {
        var key = prefix + name;
        if (value is JsonObject child)
        {
            foreach (var entry in Flatten(child, key + ".")) yield return entry;
        }
        else yield return (key, value!.ToString());
    }
}

var count = 0;
foreach (var path in Directory.GetFiles(Path.Combine(root, "Translations/zh-TW"), "*.json"))
{
    foreach (var (key, value) in Flatten(JsonNode.Parse(File.ReadAllText(path))!.AsObject()))
    {
        Check(I18N.T(key) == value, $"Catalog not loaded: {key}");
        var format = CompositeFormat.Parse(value);
        if (format.MinimumArgumentCount > 0)
            _ = string.Format(null, format, Enumerable.Repeat<object>(3, format.MinimumArgumentCount).ToArray());
        count++;
    }
}
Check(Logger.Messages.Count == 0, string.Join(Environment.NewLine, Logger.Messages));
Console.WriteLine($"PASS: {count} zh-TW values loaded by the actual Ocelot I18N implementation; aliases, switching, events, states, terms and composite formats verified.");

namespace Ocelot
{
    // The catalog loader only needs logging; no running game is required for this smoke test.
    public static class Logger
    {
        public static readonly List<string> Messages = [];
        public static void Warning(string message) => Messages.Add(message);
        public static void Error(string message) => Messages.Add(message);
    }
}
