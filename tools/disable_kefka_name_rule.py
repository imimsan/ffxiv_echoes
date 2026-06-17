# 絶ケフカのゾーン JSON で、ボス/クローンに赤円を出す「ケフカ」名前ベースの
# object_aoe_rule を無効化する（ユーザー要望: 邪魔・意味ない）。削除はせず enabled=false。
import json
import os
import shutil
from datetime import datetime

base = os.path.join(os.environ["APPDATA"], "XIVLauncher", "pluginConfigs", "FfxivEchoes")
path = os.path.join(base, "triggers", "被検世界「シグマ」V4.0.json")
backup_dir = os.path.join(base, "triggers_backup")

with open(path, encoding="utf-8") as f:
    data = json.load(f)

os.makedirs(backup_dir, exist_ok=True)
stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
shutil.copy2(path, os.path.join(backup_dir, f"被検世界「シグマ」V4.0.{stamp}.disable-kefka-rule.json"))

changed = 0
for profile in data.get("strategy_profiles") or []:
    for rule in profile.get("object_aoe_rules") or []:
        name = (rule.get("object_name") or "").strip()
        if name == "ケフカ" and rule.get("enabled", True):
            rule["enabled"] = False
            changed += 1
            print(f"disable: profile={profile.get('id')} rule id={rule.get('id')} object_name={name}")

if changed:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"written: {changed} rule(s) disabled")
else:
    print("no matching enabled rule found")
