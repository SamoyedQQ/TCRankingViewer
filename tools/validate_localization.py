"""Audit BOCCHI's shipped zh-TW catalog, including convention-based config keys.

Run: python -X utf8 tools/validate_localization.py
"""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def flatten(obj, prefix=""):
    for name, value in obj.items():
        key = prefix + name
        if isinstance(value, dict):
            yield from flatten(value, key + ".")
        else:
            assert isinstance(value, str) and value.strip(), f"Empty/non-string: {key}"
            yield key, value


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        assert key not in result, f"Duplicate JSON property: {key}"
        result[key] = value
    return result


def catalog(language):
    result = {}
    for path in sorted((ROOT / "Translations" / language).glob("*.json")):
        entries = dict(flatten(json.loads(path.read_text("utf-8-sig"), object_pairs_hook=unique_object)))
        assert not result.keys() & entries.keys(), f"Duplicate keys in {path}"
        result.update(entries)
    return result


def snake(name):
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


en, tw = catalog("en"), catalog("zh-TW")
assert not en.keys() - tw.keys(), f"Missing zh-TW: {en.keys() - tw.keys()}"
for key, original in en.items():
    placeholders = lambda s: sorted(re.findall(r"\{\d+(?:[^}]*)\}", s))
    assert placeholders(original) == placeholders(tw[key]), f"Format arguments: {key}"

# Brand and official alphanumeric mount names are deliberately preserved.
proper_names = {"windows.main.title", "game.Mount.368", "game.Mount.378"}
for key, value in tw.items():
    assert "\ufffd" not in value, f"Invalid encoding: {key}"
    assert key in proper_names or re.search(r"[\u3400-\u9fff]", value), f"Untranslated: {key}"
    if not key.startswith("game."):
        assert not re.search(r"插件|閾值|選中|鼠標|軟件|默認|魔法罐|預言家|Buff|FATE|\bCE\b", value), f"Non-Taiwan UI wording: {key}"

sources = list((ROOT / "BOCCHI").rglob("*.cs")) + list((ROOT / "Ocelot/Ocelot").rglob("*.cs"))
checked = set()
for path in sources:
    if any(part in {"bin", "obj"} for part in path.parts):
        continue
    source = re.sub(r"//[^\n]*", "", path.read_text("utf-8-sig"))
    for key in re.findall(r'I18N.T\("([^"]+)"\)', source):
        assert key in tw, f"Missing literal key {key} in {path}"
        checked.add(key)
    if path.name.endswith("Config.cs") and "BOCCHI/Modules" in path.as_posix():
        prefix = "modules." + snake(path.parent.name) + ".config."
        assert prefix + "title" in tw, f"Missing config title: {path}"
        for attributes, name in re.findall(r'((?:\s*\[[^\]]+\])+)[\s\r\n]*public [\w<>]+ (\w+)', source):
            if not re.search(r'\[(?:Checkbox|FloatRange|IntRange|Enum|MultiEnum|ExcelSheet|Color4)\b', attributes):
                continue
            label = re.search(r'\[(?:Label|LabelAndTooltip)\("([^"]+)"', attributes)
            tooltip = re.search(r'\[(?:Tooltip|LabelAndTooltip)\("([^"]+)"', attributes)
            keys = [label[1] if label else prefix + snake(name) + ".label"]
            if tooltip:
                keys.append(tooltip[1])
            elif not label:
                keys.append(prefix + snake(name) + ".tooltip")
            for key in keys:
                assert key in tw, f"Missing config key {key} in {path}"
                checked.add(key)

# Every enum value used by the production status panels must have a label.
for filename in ["BOCCHI/Modules/StateManager/State.cs", "BOCCHI/Modules/MobFarmer/States/FarmerPhase.cs"]:
    path = ROOT / filename
    for value in re.findall(r"^\s+(\w+),", path.read_text("utf-8-sig"), re.M):
        assert f"states.{path.stem}.{value}" in tw

assert '"zh-TW"' in (ROOT / "BOCCHI/Config.cs").read_text("utf-8-sig")
plugin = (ROOT / "BOCCHI/Plugin.cs").read_text("utf-8-sig")
assert 'LoadAllFromDirectory("zh-TW", "Translations/zh-TW")' in plugin
assert "Random.Shared" not in plugin, "The UI must not randomly switch languages."
print(f"PASS: {len(en)} English keys, {len(tw)} zh-TW keys, {len(checked)} explicit/config keys; no missing values or format mismatches.")
