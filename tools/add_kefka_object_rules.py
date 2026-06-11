# 絶ケフカ（被検世界「シグマ」V4.0）のゾーン JSON に床オブジェクトの object_aoe_rules を追加する。
# 録画相関で確定した data_id（氷床=2015266）と、発見用の未知 data_id マーカーを登録する。
# 実行前に triggers_backup/ へタイムスタンプ付きバックアップを取る。冪等（同 data_id 既存ならスキップ）。
import json
import os
import shutil
import sys
from datetime import datetime

base = os.path.join(os.environ["APPDATA"], "XIVLauncher", "pluginConfigs", "FfxivEchoes")
path = os.path.join(base, "triggers", "被検世界「シグマ」V4.0.json")
backup_dir = os.path.join(base, "triggers_backup")

RULES = [
    # 録画 8 本で ひろげるブリザガ 詠唱との相関 24 回を確認済み
    {"id": "kefka_ice_floor", "enabled": True, "data_id": 2015266, "label": "氷床",
     "source": "manual", "shape": "circle", "radius_m": 5.0, "duration_sec": 30.0,
     "color": "#66CCFF", "live_floor_paint": True},
    # ずびずばテレポ直後（t≈160）に複数出現。正体確認中のため控えめ表示
    {"id": "kefka_floor_2015267", "enabled": True, "data_id": 2015267, "label": "床?267",
     "source": "manual", "shape": "circle", "radius_m": 3.0, "duration_sec": 25.0,
     "color": "#CC66FF", "live_floor_paint": True},
]
# 未知の床系 data_id（録画に 2x ずつ出現）— 発見用の小さな中立マーカー
for did in (2015154, 2015155, 2015163, 2015164, 2015165):
    RULES.append({
        "id": f"kefka_floor_{did}", "enabled": True, "data_id": did,
        "label": f"床?{str(did)[-3:]}", "source": "manual", "shape": "circle",
        "radius_m": 2.0, "duration_sec": 20.0, "color": "#AAAAAA",
        "live_floor_paint": False,
    })

with open(path, encoding="utf-8") as f:
    data = json.load(f)

os.makedirs(backup_dir, exist_ok=True)
stamp = datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
backup = os.path.join(backup_dir, f"被検世界「シグマ」V4.0.{stamp}.objectrules.json")
shutil.copy2(path, backup)
print(f"backup: {backup}")

profiles = data.get("strategy_profiles") or []
active_id = data.get("active_strategy_profile_id") or "default"
profile = next((p for p in profiles if p.get("id") == active_id), None)
if profile is None:
    print(f"ERROR: active profile '{active_id}' not found", file=sys.stderr)
    sys.exit(1)

rules = profile.setdefault("object_aoe_rules", [])
existing_ids = {r.get("data_id") for r in rules if r.get("data_id")}
added = 0
for rule in RULES:
    if rule["data_id"] in existing_ids:
        print(f"skip (exists): data_id={rule['data_id']}")
        continue
    rules.append(rule)
    added += 1
    print(f"add: data_id={rule['data_id']} label={rule['label']}")

if added:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"written: {added} rules -> {path}")
else:
    print("no changes")
