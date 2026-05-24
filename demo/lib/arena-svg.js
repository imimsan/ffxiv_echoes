/* FFXIV Echoes — Arena SVG renderer
 *
 * trace.json (docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md §3)
 * の arena / draw_aoe イベントを SVG に変換するユーティリティ。
 *
 * 座標系：
 *   FFXIV ワールド： +X = 東、+Z = 南、原点はマップ依存
 *   SVG（viewBox 0..N）： +x = 右（東）、+y = 下（南）
 *   北を上に表示するため、ワールドの -Z 方向が SVG の上方向（y 小）。
 *   よって x はそのまま east 方向、y は south 方向。実は単純な平行移動 + スケールで済む。
 *
 * グローバル window.ArenaSvg として公開（ES modules は使わない）。
 */
(function (global) {
    'use strict';

    const SVG_NS = 'http://www.w3.org/2000/svg';

    // ── 形状ごとの既定色 ─────────────────────────────────────────
    const DEFAULT_COLORS = {
        donut: '#FF8800',
        circle: '#FF4444',
        rect: '#F87171',
        line: '#F87171',
        cone: '#F87171',
        fan: '#F87171',
        chevron: '#FBBF24',
    };

    /**
     * arenaSpec: { center_x, center_z, radius_m, shape, viewBoxSize? }
     * デフォルト viewBoxSize は 500。
     *
     * ワールド (x, z) を SVG (x, y) に変換する。
     * SVG 内のアリーナ中心は viewBoxSize/2、半径は arena radius が viewBoxSize/2 * marginRatio に収まるよう拡縮。
     */
    function makeProjection(arena, viewBoxSize) {
        const size = viewBoxSize || 500;
        const margin = 0.92; // SVG 内に少し余白を残す
        const arenaPx = (size / 2) * margin;
        const scale = arena.radius_m > 0 ? arenaPx / arena.radius_m : 1;
        const cx = arena.center_x;
        const cz = arena.center_z;
        const ox = size / 2;
        const oy = size / 2;

        function worldToSvg(point) {
            const dx = point.x - cx;
            const dz = point.z - cz;
            return {
                x: ox + dx * scale,
                y: oy + dz * scale, // +Z = 南 = SVG y+（下）
            };
        }

        function lengthToSvg(meters) {
            return meters * scale;
        }

        return { worldToSvg, lengthToSvg, scale, viewBoxSize: size, ox, oy };
    }

    /** 引数なしで現在表示中の projection を取り出せるよう element に attach する。 */
    function attachProjection(el, projection) {
        el.__projection = projection;
    }

    function getProjection(el) {
        return el && el.__projection;
    }

    // ── SVG node helper ──────────────────────────────────────────
    function el(tag, attrs) {
        const node = document.createElementNS(SVG_NS, tag);
        if (attrs) {
            for (const k of Object.keys(attrs)) {
                const v = attrs[k];
                if (v === undefined || v === null) continue;
                node.setAttribute(k, String(v));
            }
        }
        return node;
    }

    // ── Arena コンテナ ───────────────────────────────────────────
    /**
     * renderArena(arenaSpec, opts?) → SVGSVGElement
     * arenaSpec: { center_x, center_z, radius_m, shape, viewBoxSize? }
     * opts.viewBoxSize: override
     */
    function renderArena(arenaSpec, opts) {
        const spec = arenaSpec || { center_x: 100, center_z: 100, radius_m: 20, shape: 'circle' };
        const size = (opts && opts.viewBoxSize) || spec.viewBoxSize || 500;
        const projection = makeProjection(spec, size);

        const svg = el('svg', {
            class: 'arena-replay-svg',
            xmlns: SVG_NS,
            viewBox: `0 0 ${size} ${size}`,
            preserveAspectRatio: 'xMidYMid meet',
        });
        attachProjection(svg, projection);

        // 背景
        svg.appendChild(el('rect', {
            x: 0, y: 0, width: size, height: size,
            fill: 'rgba(15,18,24,0.95)',
        }));

        // アリーナ床
        const center = projection.worldToSvg({ x: spec.center_x, z: spec.center_z });
        const arenaPx = projection.lengthToSvg(spec.radius_m);
        const shape = (spec.shape || 'circle').toLowerCase();
        if (shape === 'square' || shape === 'rect') {
            svg.appendChild(el('rect', {
                x: center.x - arenaPx,
                y: center.y - arenaPx,
                width: arenaPx * 2,
                height: arenaPx * 2,
                fill: 'rgba(28,34,46,0.8)',
                stroke: 'rgba(255,255,255,0.18)',
                'stroke-width': 1,
            }));
        } else {
            svg.appendChild(el('circle', {
                cx: center.x,
                cy: center.y,
                r: arenaPx,
                fill: 'rgba(28,34,46,0.8)',
                stroke: 'rgba(255,255,255,0.18)',
                'stroke-width': 1,
            }));
        }

        // コンパス
        const compass = el('g', { class: 'compass', 'pointer-events': 'none' });
        compass.appendChild(el('text', {
            x: center.x, y: center.y - arenaPx - 6,
            'text-anchor': 'middle',
            'font-size': 14, fill: '#94a3b8', 'font-weight': 700,
        })).textContent = 'N';
        compass.appendChild(el('text', {
            x: center.x, y: center.y + arenaPx + 18,
            'text-anchor': 'middle',
            'font-size': 14, fill: '#475569', 'font-weight': 700,
        })).textContent = 'S';
        compass.appendChild(el('text', {
            x: center.x + arenaPx + 12, y: center.y + 5,
            'text-anchor': 'middle',
            'font-size': 14, fill: '#475569', 'font-weight': 700,
        })).textContent = 'E';
        compass.appendChild(el('text', {
            x: center.x - arenaPx - 12, y: center.y + 5,
            'text-anchor': 'middle',
            'font-size': 14, fill: '#475569', 'font-weight': 700,
        })).textContent = 'W';
        svg.appendChild(compass);

        // 中心マーカー
        svg.appendChild(el('circle', {
            cx: center.x, cy: center.y, r: 2,
            fill: 'rgba(255,255,255,0.35)',
        }));

        // AoE をまとめる group（呼び出し側が clear して使う）
        const aoeLayer = el('g', { class: 'aoe-layer' });
        svg.appendChild(aoeLayer);

        // actor layer（ボス/object の現在位置等、将来）
        const actorLayer = el('g', { class: 'actor-layer' });
        svg.appendChild(actorLayer);

        // 参照しやすいよう DOM に保持
        svg.__aoeLayer = aoeLayer;
        svg.__actorLayer = actorLayer;
        svg.__arenaSpec = spec;

        return svg;
    }

    function getAoeLayer(svg) {
        return svg && svg.__aoeLayer;
    }

    function getActorLayer(svg) {
        return svg && svg.__actorLayer;
    }

    // ── AoE 形状 ─────────────────────────────────────────────────
    /**
     * renderAoe(aoe, arenaSpec, opts?) → SVGGElement
     *
     * aoe payload は trace.json §3 の draw_aoe event。
     * 主なフィールド:
     *   shape: 'donut' | 'circle' | 'rect' | 'cone' | 'fan' | 'line' | 'chevron'
     *   x_world / z_world  （優先）または x_relative / z_relative （arena center に対する相対）
     *   rotation_rad
     *   radius_m / inner_radius_m / fan_deg / half_width_m / length_m
     *   color  （指定なければ shape ごとの既定色）
     *   label
     *   id
     */
    function renderAoe(aoe, arenaSpec, opts) {
        const size = (opts && opts.viewBoxSize) || arenaSpec.viewBoxSize || 500;
        const projection = makeProjection(arenaSpec, size);
        const shape = String(aoe.shape || 'circle').toLowerCase();
        const color = aoe.color || DEFAULT_COLORS[shape] || '#FBBF24';

        // 中心ワールド座標を決める
        let wx, wz;
        if (typeof aoe.x_world === 'number' && typeof aoe.z_world === 'number') {
            wx = aoe.x_world;
            wz = aoe.z_world;
        } else if (typeof aoe.x_relative === 'number' && typeof aoe.z_relative === 'number') {
            wx = arenaSpec.center_x + aoe.x_relative;
            wz = arenaSpec.center_z + aoe.z_relative;
        } else if (typeof aoe.x === 'number' && typeof aoe.z === 'number') {
            wx = aoe.x;
            wz = aoe.z;
        } else {
            wx = arenaSpec.center_x;
            wz = arenaSpec.center_z;
        }

        const center = projection.worldToSvg({ x: wx, z: wz });
        const rotationRad = typeof aoe.rotation_rad === 'number'
            ? aoe.rotation_rad
            : (typeof aoe.rotationRad === 'number' ? aoe.rotationRad : 0);
        // FFXIV の rotation はゲーム内で +Z 方向（南）が 0 で時計回りに π/2 … が西、らしいが
        // ここでは「rotation=0 が南向き、+方向が時計回り（東→北→西→南）」前提で扱う。
        // SVG y+ が南なので degrees に変換すると 0 rad → 0deg 回転（=下向き）。

        const g = el('g', {
            class: `aoe aoe-${shape}`,
            'data-aoe-id': aoe.id || '',
            'data-service': aoe.service || '',
        });

        const fillAlpha = aoe.fill_alpha != null ? aoe.fill_alpha : 0.35;
        const fillColor = withAlpha(color, fillAlpha);
        const strokeColor = color;

        switch (shape) {
            case 'circle': {
                const r = projection.lengthToSvg(numOrDefault(aoe.radius_m, 5));
                g.appendChild(el('circle', {
                    cx: center.x, cy: center.y, r,
                    fill: fillColor,
                    stroke: strokeColor,
                    'stroke-width': 2,
                }));
                break;
            }
            case 'donut': {
                const outer = projection.lengthToSvg(numOrDefault(aoe.radius_m, 6));
                const inner = projection.lengthToSvg(numOrDefault(aoe.inner_radius_m, 2));
                // donut は外周円 + 内側円（くり抜き）を 1 つの path で表す
                const d = donutPath(center.x, center.y, outer, inner);
                g.appendChild(el('path', {
                    d,
                    'fill-rule': 'evenodd',
                    fill: fillColor,
                    stroke: strokeColor,
                    'stroke-width': 2,
                }));
                break;
            }
            case 'rect':
            case 'line': {
                const halfW = projection.lengthToSvg(numOrDefault(aoe.half_width_m, aoe.x_axis_modifier || 2));
                const length = projection.lengthToSvg(numOrDefault(aoe.length_m, aoe.radius_m || 20));
                // 矩形 AoE は原点側が actor 位置、length 方向が射出方向。
                // SVG では中心 (cx, cy)、幅 2*halfW、高さ length、上端を actor とする。
                // rotation=0 が南向きなので、初期 path は下方向に伸ばす。
                const rect = el('rect', {
                    x: -halfW,
                    y: 0,
                    width: halfW * 2,
                    height: length,
                    fill: fillColor,
                    stroke: strokeColor,
                    'stroke-width': 2,
                });
                const wrapper = el('g', {
                    transform: `translate(${center.x},${center.y}) rotate(${radToDeg(rotationRad)})`,
                });
                wrapper.appendChild(rect);
                g.appendChild(wrapper);
                break;
            }
            case 'cone':
            case 'fan': {
                const r = projection.lengthToSvg(numOrDefault(aoe.radius_m, 10));
                const fanDeg = numOrDefault(aoe.fan_deg, 90);
                // rotation=0 が南向き。fan の中心線を rotation 方向に向け、左右に fanDeg/2 ずつ広げる。
                // SVG で「下向き」=（angle = 90 deg, つまり Math.cos/sin 換算で x=0,y=1）
                const startAngle = 90 + radToDeg(rotationRad) - fanDeg / 2;
                const endAngle = 90 + radToDeg(rotationRad) + fanDeg / 2;
                const d = fanPath(center.x, center.y, r, startAngle, endAngle);
                g.appendChild(el('path', {
                    d,
                    fill: fillColor,
                    stroke: strokeColor,
                    'stroke-width': 2,
                }));
                break;
            }
            case 'chevron': {
                // 向き矢印（簡易）— rotation 方向に小さな三角形
                const len = projection.lengthToSvg(numOrDefault(aoe.length_m, 4));
                const halfW = projection.lengthToSvg(numOrDefault(aoe.half_width_m, 2));
                const wrapper = el('g', {
                    transform: `translate(${center.x},${center.y}) rotate(${radToDeg(rotationRad)})`,
                });
                wrapper.appendChild(el('polygon', {
                    points: `0,0 ${-halfW},${len} ${halfW},${len}`,
                    fill: fillColor,
                    stroke: strokeColor,
                    'stroke-width': 1.5,
                }));
                g.appendChild(wrapper);
                break;
            }
            default: {
                // 未対応形状はマーカー + テキスト
                g.appendChild(el('circle', {
                    cx: center.x, cy: center.y, r: 6,
                    fill: 'transparent', stroke: strokeColor, 'stroke-dasharray': '3 2',
                }));
                const t = el('text', {
                    x: center.x + 8, y: center.y - 4,
                    'font-size': 10, fill: strokeColor,
                });
                t.textContent = `?${shape}`;
                g.appendChild(t);
            }
        }

        // anchor 表示（actor / static 区別）
        if (aoe.anchor === 'actor') {
            g.appendChild(el('circle', {
                cx: center.x, cy: center.y, r: 3,
                fill: '#fff', stroke: strokeColor, 'stroke-width': 1,
            }));
        }

        // ラベル
        if (aoe.label) {
            const t = el('text', {
                x: center.x, y: center.y - 6,
                'text-anchor': 'middle',
                'font-size': 10, fill: '#e2e8f0',
                'pointer-events': 'none',
            });
            t.textContent = aoe.label;
            g.appendChild(t);
        }

        return g;
    }

    // ── Path helpers ─────────────────────────────────────────────
    function donutPath(cx, cy, outerR, innerR) {
        // 外円（時計回り）+ 内円（反時計回り）で 'evenodd' を活かす
        const o = outerR;
        const i = Math.max(0, Math.min(innerR, outerR - 0.5));
        return [
            `M ${cx - o} ${cy}`,
            `a ${o} ${o} 0 1 0 ${o * 2} 0`,
            `a ${o} ${o} 0 1 0 ${-o * 2} 0`,
            `Z`,
            `M ${cx - i} ${cy}`,
            `a ${i} ${i} 0 1 1 ${i * 2} 0`,
            `a ${i} ${i} 0 1 1 ${-i * 2} 0`,
            `Z`,
        ].join(' ');
    }

    function fanPath(cx, cy, r, startDeg, endDeg) {
        const a1 = degToRad(startDeg);
        const a2 = degToRad(endDeg);
        const x1 = cx + r * Math.cos(a1);
        const y1 = cy + r * Math.sin(a1);
        const x2 = cx + r * Math.cos(a2);
        const y2 = cy + r * Math.sin(a2);
        const largeArc = (endDeg - startDeg) > 180 ? 1 : 0;
        return `M ${cx} ${cy} L ${x1.toFixed(2)} ${y1.toFixed(2)} A ${r} ${r} 0 ${largeArc} 1 ${x2.toFixed(2)} ${y2.toFixed(2)} Z`;
    }

    function withAlpha(color, alpha) {
        // #RRGGBB → rgba(...). 既に rgba/rgb の場合はそのまま。
        if (!color) return `rgba(255,255,255,${alpha})`;
        if (color.startsWith('rgba') || color.startsWith('rgb')) return color;
        if (color.startsWith('#') && (color.length === 7 || color.length === 4)) {
            let r, g, b;
            if (color.length === 7) {
                r = parseInt(color.slice(1, 3), 16);
                g = parseInt(color.slice(3, 5), 16);
                b = parseInt(color.slice(5, 7), 16);
            } else {
                r = parseInt(color.charAt(1) + color.charAt(1), 16);
                g = parseInt(color.charAt(2) + color.charAt(2), 16);
                b = parseInt(color.charAt(3) + color.charAt(3), 16);
            }
            return `rgba(${r},${g},${b},${alpha})`;
        }
        return color;
    }

    function radToDeg(r) { return r * 180 / Math.PI; }
    function degToRad(d) { return d * Math.PI / 180; }
    function numOrDefault(v, def) { return typeof v === 'number' && !isNaN(v) ? v : def; }

    // ── 公開する worldToSvg（外部ツール用） ────────────────────────
    function worldToSvg(point, arenaSpec, viewBoxSize) {
        const projection = makeProjection(arenaSpec, viewBoxSize || 500);
        return projection.worldToSvg(point);
    }

    // ── Sanity check ─────────────────────────────────────────────
    (function selfTest() {
        const arena = { center_x: 100, center_z: 100, radius_m: 20, shape: 'circle' };
        const c = worldToSvg({ x: 100, z: 100 }, arena, 500);
        console.assert(Math.abs(c.x - 250) < 0.01, 'arena center should map to SVG center x');
        console.assert(Math.abs(c.y - 250) < 0.01, 'arena center should map to SVG center y');
        const south = worldToSvg({ x: 100, z: 120 }, arena, 500);
        console.assert(south.y > c.y, '+Z direction (south) should be SVG y+ (down)');
        const east = worldToSvg({ x: 120, z: 100 }, arena, 500);
        console.assert(east.x > c.x, '+X direction (east) should be SVG x+ (right)');
    })();

    // ── Export ───────────────────────────────────────────────────
    global.ArenaSvg = {
        renderArena,
        renderAoe,
        worldToSvg,
        getAoeLayer,
        getActorLayer,
        DEFAULT_COLORS,
    };
})(window);
