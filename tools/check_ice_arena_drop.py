# 氷床(2015266)の出現座標がアリーナ判定（中心95,99 / 半径19）で弾かれるか検証する一時スクリプト
import glob
import json
import math
import os

d = os.path.join(os.environ["APPDATA"], "XIVLauncher", "pluginConfigs", "FfxivEchoes",
                 "recordings", "被検世界「シグマ」V4.0")
cx, cz, r = 95.0, 99.0, 19.0
total = dropped = 0
for p in sorted(glob.glob(os.path.join(d, "*.jsonl")))[-8:]:
    with open(p, encoding="utf-8") as f:
        for line in f:
            try:
                j = json.loads(line)
            except Exception:
                continue
            if j.get("type") == "object_appear" and j.get("data_id") == 2015266:
                pos = j["position"]
                dist = math.hypot(pos["x"] - cx, pos["z"] - cz)
                total += 1
                mark = "DROP" if dist > r else "ok  "
                print(f'{mark} ({pos["x"]:7.2f},{pos["z"]:7.2f}) dist={dist:5.1f}')
                if dist > r:
                    dropped += 1
print(f"ice total={total} dropped_by_arena_check={dropped}")
