/* FFXIV Echoes Web Demo
 * 実プラグインの挙動を CSS / JS で簡略再現したシミュレータ。
 * - 戦闘相対秒のクロック
 * - 事前定義されたボスキャストのスケジュール → トリガーマッチ → アクション実行
 * - TimelineNote の advance_warning 通知
 * - LiveTimelineWindow / OverlayWindow の見た目
 */

(() => {
    'use strict';

    // ── サンプル JSON（fetch 失敗時のフォールバックで使う） ─────────
    const FALLBACK_JSON = {
        version: '1.0', zone: 'Demo',
        auto_settings: { enable_triggers: true, show_timeline: true },
        sync_points: [], triggers: [], notes: [], _demo_boss_casts: []
    };

    // ── 状態 ──────────────────────────────────────────────────────────
    let config = null;
    let combatStartedAt = null;     // performance.now() ベース、null なら戦闘外
    let speed = 1.0;
    let role = 'tank';
    let rafId = null;

    // 現フレームでのスケジュール状況
    const firedCasts = new Set();        // ボスキャスト ID（demo_boss_casts のインデックス）
    const firedNoteWarnings = new Set(); // ノート ID（先行通知済み）
    const activeOverlayTexts = [];       // { text, color, size, expiresAt, fadeOutAt }
    const activeTimerBars = [];          // { label, color, startedAt, duration, warnAt }
    const activeCasts = [];              // 現在キャスト中のボスキャスト（タイムライン青ドット用）
    const activeArenaItems = [];         // { gimmick, callout, direction, fanDeg, expiresAt } - arena_view 由来

    // ── DOM ────────────────────────────────────────────────────────────
    const $ = (id) => document.getElementById(id);
    const elClock = $('clock');
    const elBtnToggle = $('btn-toggle');
    const elBtnReset = $('btn-reset');
    const elBtnApply = $('btn-apply');
    const elBtnLoadSample = $('btn-load-sample');
    const elSpeed = $('speed');
    const elRole = $('role');
    const elJson = $('config-json');
    const elApplyStatus = $('apply-status');
    const elTimeline = $('live-timeline');
    const elTimers = $('timer-bars');
    const elChat = $('chat-log');
    const elUpcoming = $('upcoming-list');
    const elArena = $('arena-view');

    // ── ユーティリティ ─────────────────────────────────────────────────
    const nowSec = () => combatStartedAt === null
        ? null
        : (performance.now() - combatStartedAt) / 1000.0 * speed;

    const formatTime = (sec) => {
        if (sec === null) return '0:00.0';
        const m = Math.floor(sec / 60);
        const s = sec - m * 60;
        return `${m}:${s.toFixed(1).padStart(4, '0')}`;
    };

    const log = (text, cls = '') => {
        const t = nowSec();
        const ts = t === null ? '----' : formatTime(t);
        const line = document.createElement('div');
        line.className = `chat-line ${cls}`;
        line.innerHTML = `<span class="ts">[t=${ts}]</span>${escape(text)}`;
        elChat.appendChild(line);
        elChat.scrollTop = elChat.scrollHeight;
        // 古い行は消す（200 行制限）
        while (elChat.children.length > 200) {
            elChat.removeChild(elChat.firstChild);
        }
    };

    const escape = (s) => String(s).replace(/[&<>]/g, (c) =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

    /**
     * icon フィールドを HTML に変換。string でも array でも受ける。
     * 各要素は絵文字 or 画像 URL（http(s)://〜）。
     * 配列の場合は横並びで全部表示する（例：ランパート + ブラインド）。
     */
    const renderIcons = (icon) => {
        if (!icon) return '';
        const list = Array.isArray(icon) ? icon : [icon];
        return list.map((entry) => {
            if (typeof entry !== 'string' || !entry) return '';
            const isUrl = /^https?:\/\//.test(entry);
            return isUrl
                ? `<img src="${escape(entry)}" alt="" class="icon-img">`
                : `<span class="icon-emoji">${escape(entry)}</span>`;
        }).join('');
    };

    // role 判定（プラグイン側 NoteReminderService.MatchesPlayer と同等）
    const noteMatchesPlayer = (note) => {
        if (note.role) {
            const r = note.role.toLowerCase();
            if (r === 'any') return true;
            if (['tank', 'mt', 'st'].includes(r) && role !== 'tank') return false;
            if (['healer', 'h1', 'h2'].includes(r) && role !== 'healer') return false;
            if (['dps', 'melee', 'ranged', 'caster'].includes(r) && role !== 'dps') return false;
        }
        return true;
    };

    // ── アクション実行（簡易ディスパッチ） ─────────────────────────────
    const dispatchActions = (actions) => {
        if (!Array.isArray(actions)) return;
        actions.forEach((action) => {
            const delayMs = (action.delay || 0) * 1000;
            const exec = () => {
                switch (action.type) {
                    case 'tts':
                        log(action.text || '', 'tts');
                        break;
                    case 'chat_echo':
                        log(action.text || '', '');
                        break;
                    case 'overlay_text':
                        // 中央オーバーレイは画面が埋まるためデモでは省略。
                        // チャットログに「警告」として表示。
                        log(`⚠️  ${action.text || ''}`, 'tts');
                        break;
                    case 'timer_bar':
                        addTimerBar(action.label || '', action.duration || 0, action.color, action.warn_at);
                        break;
                    case 'arena_view':
                        addArenaItem(action.gimmick, action.callout, action.duration || 5,
                            action.direction, action.fan_deg);
                        break;
                    default:
                        log(`(unimplemented action: ${action.type})`, '');
                }
            };
            if (delayMs > 0) setTimeout(exec, delayMs);
            else exec();
        });
    };

    const addOverlayText = (text, duration, color, size) => {
        const item = {
            text, color: color || '#FFFFFF', size: size || 'medium',
            startedAt: performance.now(),
            duration: duration * 1000,
        };
        activeOverlayTexts.push(item);
    };

    const addTimerBar = (label, duration, color, warnAt) => {
        activeTimerBars.push({
            label, color: color || '#FBBF24',
            startedAt: performance.now(),
            duration: duration * 1000,
            warnAt: (warnAt || 0) * 1000,
        });
    };

    /**
     * arena_view アクション。プラグイン側 ArenaViewHandler と同等。
     * gimmick: outer_ring / inner_circle / scatter / stack / cone
     */
    const addArenaItem = (gimmick, callout, duration, direction, fanDeg) => {
        if (!gimmick) return;
        activeArenaItems.push({
            gimmick: String(gimmick).toLowerCase(),
            callout: callout || '',
            direction: direction || null,
            fanDeg: typeof fanDeg === 'number' ? fanDeg : 90,
            expiresAt: performance.now() + (duration > 0 ? duration : 5) * 1000,
        });
    };

    // ── ボスキャスト → トリガーマッチ ───────────────────────────────
    const checkBossCasts = (now) => {
        if (!config?._demo_boss_casts) return;
        config._demo_boss_casts.forEach((cast, idx) => {
            if (firedCasts.has(idx)) return;
            if (now >= cast.time) {
                firedCasts.add(idx);
                log(`Boss キャスト開始：${cast.cast_name} (${cast.cast_time}s)`, 'cast');
                activeCasts.push({
                    id: idx,
                    cast_id: cast.cast_id,
                    cast_name: cast.cast_name,
                    startedAt: cast.time,
                    castTime: cast.cast_time,
                });
                // マッチするトリガーを探して発火
                config.triggers?.forEach((trigger) => {
                    if (!trigger.enabled && trigger.enabled !== undefined) return;
                    if (trigger.type !== 'cast_start') return;
                    if (!matchCast(trigger.match, cast)) return;
                    log(`⚡ Trigger: ${trigger.id}${trigger.name ? ` (${trigger.name})` : ''}`, '');
                    dispatchActions(trigger.actions);
                });
            }
        });
        // キャスト完了したものを active から外す
        for (let i = activeCasts.length - 1; i >= 0; i--) {
            const c = activeCasts[i];
            if (now >= c.startedAt + c.castTime) {
                activeCasts.splice(i, 1);
            }
        }
    };

    const matchCast = (m, cast) => {
        if (!m) return true;
        if (m.cast_id && m.cast_id !== cast.cast_id) return false;
        if (m.cast_name && m.cast_name !== cast.cast_name) return false;
        return true;
    };

    // ── ノートの advance_warning を発火 ───────────────────────────────
    const checkNoteWarnings = (now) => {
        if (!config?.notes) return;
        config.notes.forEach((note) => {
            if (firedNoteWarnings.has(note.id)) return;
            if (!note.advance_warning_sec || note.advance_warning_sec <= 0) return;
            if (!noteMatchesPlayer(note)) return;
            const fireAt = note.time - note.advance_warning_sec;
            if (now >= fireAt) {
                firedNoteWarnings.add(note.id);
                const text = note.warning_text || note.label;
                log(`📌 ${text}`, 'note-fire');
                dispatchActions([
                    { type: 'tts', text },
                    { type: 'overlay_text', text, duration: 4, color: note.color },
                ]);
            }
        });
    };

    // ── タイムライン描画（毎フレーム） ─────────────────────────────────
    const PAST = 10, FUTURE = 30;

    const renderTimeline = () => {
        const now = nowSec() ?? 0;
        const minSec = now - PAST, maxSec = now + FUTURE, span = maxSec - minSec;
        const w = elTimeline.clientWidth;

        // クリア
        elTimeline.innerHTML = '';

        // 現在ライン
        const nowLine = document.createElement('div');
        nowLine.className = 'timeline-now';
        const nowX = ((now - minSec) / span) * w;
        nowLine.style.left = `${nowX}px`;
        elTimeline.appendChild(nowLine);

        // 5 秒刻みの目盛り
        const axis = document.createElement('div');
        axis.className = 'timeline-axis';
        const startTick = Math.floor(minSec / 5) * 5;
        for (let s = startTick; s <= maxSec; s += 5) {
            const x = ((s - minSec) / span) * w;
            const tick = document.createElement('div');
            tick.className = 'timeline-tick';
            tick.style.left = `${x}px`;
            axis.appendChild(tick);
            const lbl = document.createElement('div');
            lbl.className = 'timeline-tick-label';
            lbl.style.left = `${x}px`;
            lbl.textContent = `${s}s`;
            axis.appendChild(lbl);
        }
        elTimeline.appendChild(axis);

        // SyncPoints
        config?.sync_points?.forEach((sp) => {
            if (sp.expected_time < minSec || sp.expected_time > maxSec) return;
            const x = ((sp.expected_time - minSec) / span) * w;
            const ev = document.createElement('div');
            ev.className = 'timeline-event sync';
            ev.style.left = `${x}px`;
            ev.innerHTML = `<div class="timeline-event-line"></div><div>${escape(sp.id)}</div>`;
            elTimeline.appendChild(ev);
        });

        // Notes
        config?.notes?.forEach((note) => {
            if (note.time < minSec - (note.duration || 0) || note.time > maxSec) return;
            const x = ((note.time - minSec) / span) * w;
            const ev = document.createElement('div');
            ev.className = 'timeline-event note';
            if (!noteMatchesPlayer(note)) ev.classList.add('muted');
            if (note.color) ev.style.color = note.color;
            ev.style.left = `${x}px`;

            let bar = '';
            if (note.duration && note.duration > 0) {
                const x2 = ((note.time + note.duration - minSec) / span) * w;
                const widthPx = Math.max(2, x2 - x);
                bar = `<div class="timeline-event-bar" style="width:${widthPx}px"></div>`;
            }
            ev.innerHTML = `<div class="timeline-event-line"></div><div>${escape(note.label || note.id)}</div>${bar}`;
            elTimeline.appendChild(ev);
        });

        // 進行中のキャスト（青ドット、現在時刻にかぶる範囲のみ）
        activeCasts.forEach((c) => {
            const start = c.startedAt;
            const end = c.startedAt + c.castTime;
            if (end < minSec || start > maxSec) return;
            const x = ((start - minSec) / span) * w;
            const ev = document.createElement('div');
            ev.className = 'timeline-event cast';
            ev.style.left = `${x}px`;
            ev.style.bottom = '70px';
            ev.innerHTML = `<div class="timeline-event-dot"></div><div>${escape(c.cast_name)}</div>`;
            elTimeline.appendChild(ev);
        });
    };

    // ── オーバーレイ描画（タイマーバーのみ） ────────────────────────────
    const renderOverlays = () => {
        const now = performance.now();

        // 期限切れを除去
        for (let i = activeTimerBars.length - 1; i >= 0; i--) {
            if (now > activeTimerBars[i].startedAt + activeTimerBars[i].duration) {
                activeTimerBars.splice(i, 1);
            }
        }

        // タイマーバー
        elTimers.innerHTML = '';
        activeTimerBars.forEach((item) => {
            const elapsed = now - item.startedAt;
            const remaining = item.duration - elapsed;
            const fraction = Math.max(0, Math.min(1, remaining / item.duration));
            const inWarn = item.warnAt > 0 && remaining <= item.warnAt;

            const div = document.createElement('div');
            div.className = 'timer-bar' + (inWarn ? ' warn' : '');
            div.innerHTML = `
                <div class="label"><span>${escape(item.label)}</span><span>${(remaining / 1000).toFixed(1)}s</span></div>
                <div class="bar-bg"><div class="bar-fill" style="width:${fraction * 100}%; background:${item.color}"></div></div>
            `;
            elTimers.appendChild(div);
        });
    };

    // ── アリーナ図描画（特定ギミック発生中のみ） ──────────────────────
    /**
     * activeArenaItems（arena_view アクション由来、プラグインと同モデル）を優先。
     * 加えてレガシー：boss_cast.gimmick が設定されていればキャスト中のみ表示。
     * 複数同時なら残時間が短いものを優先。
     */
    const renderArena = () => {
        const now = nowSec();
        if (now === null) {
            elArena.classList.remove('active');
            elArena.innerHTML = '';
            return;
        }

        const perfNow = performance.now();
        // 期限切れを除去
        for (let i = activeArenaItems.length - 1; i >= 0; i--) {
            if (perfNow > activeArenaItems[i].expiresAt) activeArenaItems.splice(i, 1);
        }

        let target = null;

        // 1. arena_view アクション由来（プラグイン互換）
        for (const it of activeArenaItems) {
            const remaining = (it.expiresAt - perfNow) / 1000;
            const candidate = {
                gimmick: { type: it.gimmick, callout: it.callout, direction: it.direction, fan_deg: it.fanDeg },
                source: 'action',
                label: it.callout,
                remaining,
            };
            if (!target || candidate.remaining < target.remaining) target = candidate;
        }

        // 2. レガシー：boss_cast.gimmick（_demo_boss_casts に直接書かれた場合）
        for (const c of activeCasts) {
            const cast = config?._demo_boss_casts?.[c.id];
            if (!cast || !cast.gimmick) continue;
            const remaining = (c.startedAt + c.castTime) - now;
            const candidate = { gimmick: cast.gimmick, source: 'cast', label: cast.cast_name, remaining };
            if (!target || candidate.remaining < target.remaining) target = candidate;
        }

        if (!target) {
            elArena.classList.remove('active');
            elArena.innerHTML = '';
            return;
        }

        const svg = renderArenaSvg(target.gimmick);
        const cd = Math.max(0, target.remaining).toFixed(1);
        elArena.innerHTML = `
            ${svg}
            <div class="arena-callout">
                <div class="callout-text">${escape(target.gimmick.callout || '')}</div>
                <div class="callout-cast">${escape(target.label)} · ${cd}s</div>
            </div>
        `;
        elArena.classList.add('active');
    };

    /** ギミック種別ごとにアリーナ SVG を組み立てる。 */
    const renderArenaSvg = (g) => {
        const arena = `<circle cx="100" cy="100" r="95" fill="rgba(20,25,35,0.82)" stroke="rgba(255,255,255,0.18)" stroke-width="1"/>`;
        let body = '';
        let boss = `<circle cx="100" cy="100" r="5" fill="#fb923c" stroke="#fff" stroke-width="1.2"/>`;

        switch (g.type) {
            case 'outer_ring':
                // 外周が危険、中央が安置（無の肥大タイプ）
                body = `
                    <circle cx="100" cy="100" r="92" fill="rgba(248,113,113,0.42)" stroke="#f87171" stroke-width="1.5"/>
                    <circle cx="100" cy="100" r="48" fill="rgba(34,197,94,0.5)" stroke="#34D399" stroke-width="2.5" stroke-dasharray="6 3"/>
                    <text x="100" y="128" text-anchor="middle" font-size="12" fill="#86efac" font-weight="700" letter-spacing="1">SAFE</text>
                `;
                break;
            case 'inner_circle':
                // 中央が危険、外周が安置（円形 AoE）
                body = `
                    <circle cx="100" cy="100" r="55" fill="rgba(248,113,113,0.55)" stroke="#f87171" stroke-width="2"/>
                    <text x="100" y="103" text-anchor="middle" font-size="20" fill="#fff" font-weight="700">!</text>
                    <text x="100" y="180" text-anchor="middle" font-size="11" fill="#86efac" font-weight="700">外周安置</text>
                `;
                break;
            case 'scatter': {
                // 4 方向散開
                const positions = [
                    { x: 100, y: 30, label: 'N' },
                    { x: 170, y: 100, label: 'E' },
                    { x: 100, y: 170, label: 'S' },
                    { x: 30, y: 100, label: 'W' },
                ];
                body = positions.map(p => `
                    <circle cx="${p.x}" cy="${p.y}" r="13" fill="rgba(96,165,250,0.42)" stroke="#60A5FA" stroke-width="2"/>
                    <text x="${p.x}" y="${p.y + 4}" text-anchor="middle" font-size="11" fill="#bfdbfe" font-weight="700">${p.label}</text>
                `).join('');
                boss = `
                    <circle cx="100" cy="100" r="6" fill="#f87171" stroke="#fff" stroke-width="1.5"/>
                    <text x="100" y="103" text-anchor="middle" font-size="9" fill="#fff" font-weight="700">!</text>
                `;
                break;
            }
            case 'stack':
                // 中央集合
                body = `
                    <circle cx="100" cy="100" r="38" fill="rgba(96,165,250,0.4)" stroke="#60A5FA" stroke-width="2.5" stroke-dasharray="5 3"/>
                    <text x="100" y="135" text-anchor="middle" font-size="11" fill="#bfdbfe" font-weight="700">STACK</text>
                `;
                break;
            case 'cone': {
                // 指定方向への扇形コーン
                const dirAngles = { N: -90, NE: -45, E: 0, SE: 45, S: 90, SW: 135, W: 180, NW: -135 };
                const angle = (dirAngles[g.direction] ?? -90) * Math.PI / 180;
                const halfFan = ((g.fan_deg || 90) * Math.PI) / 360;
                const cx = 100, cy = 100, r = 95;
                const a1 = angle - halfFan, a2 = angle + halfFan;
                const x1 = cx + r * Math.cos(a1);
                const y1 = cy + r * Math.sin(a1);
                const x2 = cx + r * Math.cos(a2);
                const y2 = cy + r * Math.sin(a2);
                body = `<path d="M ${cx} ${cy} L ${x1.toFixed(1)} ${y1.toFixed(1)} A ${r} ${r} 0 0 1 ${x2.toFixed(1)} ${y2.toFixed(1)} Z" fill="rgba(248,113,113,0.5)" stroke="#f87171" stroke-width="1.5"/>`;
                break;
            }
            default:
                body = `<text x="100" y="105" text-anchor="middle" font-size="12" fill="#94a3b8">unknown gimmick: ${escape(g.type || '')}</text>`;
        }

        return `<svg class="arena-svg" viewBox="0 0 200 200">${arena}${body}${boss}</svg>`;
    };

    // ── 次に来るイベントリスト（カウントダウン + アイコン） ─────────────
    const renderUpcoming = () => {
        const now = nowSec() ?? 0;
        const items = [];

        // ボスキャスト
        config?._demo_boss_casts?.forEach((c) => {
            if (c.time < now - 1) return;
            items.push({
                kind: 'cast',
                time: c.time,
                icon: c.icon || '⚡',
                label: c.cast_name,
                sub: `cast ${c.cast_time}s`,
                color: '#60A5FA',
                muted: false,
            });
        });

        // SyncPoint
        config?.sync_points?.forEach((sp) => {
            if (sp.expected_time < now - 1) return;
            items.push({
                kind: 'sync',
                time: sp.expected_time,
                icon: '🔖',
                label: sp.id,
                sub: 'sync',
                color: '#4ADE80',
                muted: false,
            });
        });

        // Note
        config?.notes?.forEach((note) => {
            if (note.time < now - 1) return;
            const matches = noteMatchesPlayer(note);
            items.push({
                kind: 'note',
                time: note.time,
                icon: note.icon || '📌',
                label: note.label || note.id,
                sub: note.role ? `role: ${note.role}` : (note.duration ? `${note.duration}s` : 'note'),
                color: note.color || '#FCD34D',
                muted: !matches,
                advance: note.advance_warning_sec,
            });
        });

        items.sort((a, b) => a.time - b.time);
        const visible = items.slice(0, 6);

        elUpcoming.innerHTML = '';
        visible.forEach((item) => {
            const remaining = item.time - now;
            const imminent = remaining <= 5 && remaining >= -0.5 && !item.muted;
            const row = document.createElement('div');
            row.className = 'upcoming-row';
            if (imminent) row.classList.add('imminent');
            if (item.muted) row.classList.add('muted-role');
            row.style.borderLeftColor = item.color;

            const iconsHtml = renderIcons(item.icon);
            const cdStr = remaining < 0
                ? `+${(-remaining).toFixed(1)}s`
                : `${remaining.toFixed(1)}s`;

            row.innerHTML = `
                <div class="icons">${iconsHtml}</div>
                <div class="countdown">${cdStr}</div>
                <div>
                    <div class="label">${escape(item.label)}</div>
                    <div class="sub">${escape(item.sub)}</div>
                </div>
            `;
            elUpcoming.appendChild(row);
        });
    };

    // ── メインループ ──────────────────────────────────────────────────
    const tick = () => {
        const t = nowSec();
        elClock.textContent = formatTime(t);
        if (t !== null) {
            checkBossCasts(t);
            checkNoteWarnings(t);
        }
        renderTimeline();
        renderOverlays();
        renderUpcoming();
        renderArena();
        rafId = requestAnimationFrame(tick);
    };

    // ── 戦闘ライフサイクル ─────────────────────────────────────────────
    const startCombat = () => {
        combatStartedAt = performance.now();
        firedCasts.clear();
        firedNoteWarnings.clear();
        activeCasts.length = 0;
        elBtnToggle.textContent = '⏸ 一時停止';
        log('=== 戦闘開始 ===', 'combat');
    };

    const stopCombat = () => {
        combatStartedAt = null;
        elBtnToggle.textContent = '▶ 戦闘開始';
        activeOverlayTexts.length = 0;
        activeTimerBars.length = 0;
        activeCasts.length = 0;
        activeArenaItems.length = 0;
        elArena.classList.remove('active');
        elArena.innerHTML = '';
        log('=== 戦闘終了 ===', 'combat');
    };

    const resetAll = () => {
        stopCombat();
        firedCasts.clear();
        firedNoteWarnings.clear();
        elClock.textContent = '0:00.0';
        elChat.innerHTML = '';
        renderTimeline();
        renderOverlays();
        renderUpcoming();
        renderArena();
    };

    // ── 設定読み込み ──────────────────────────────────────────────────
    const applyConfigText = (text) => {
        try {
            const parsed = JSON.parse(text);
            config = parsed;
            elApplyStatus.textContent = '✓ 適用しました';
            elApplyStatus.className = 'small ok';
            return true;
        } catch (e) {
            elApplyStatus.textContent = `JSON エラー：${e.message}`;
            elApplyStatus.className = 'small err';
            return false;
        }
    };

    const loadSample = async () => {
        try {
            const res = await fetch('sample-config.json');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const text = await res.text();
            elJson.value = text;
            applyConfigText(text);
            log('sample-config.json を読み込みました', '');
        } catch (e) {
            // file:// プロトコルでは fetch が失敗するので、その場合はインラインフォールバック
            console.warn('sample-config.json の fetch に失敗：', e);
            elJson.value = JSON.stringify(FALLBACK_JSON, null, 2);
            applyConfigText(elJson.value);
            log('（fetch 失敗。file:// で開いている場合はローカルサーバ経由で開いてください）', '');
        }
    };

    // ── イベントハンドラ ──────────────────────────────────────────────
    elBtnToggle.addEventListener('click', () => {
        if (combatStartedAt === null) startCombat();
        else stopCombat();
    });
    elBtnReset.addEventListener('click', resetAll);
    elBtnApply.addEventListener('click', () => {
        if (applyConfigText(elJson.value)) {
            firedCasts.clear();
            firedNoteWarnings.clear();
        }
    });
    elBtnLoadSample.addEventListener('click', loadSample);
    elSpeed.addEventListener('change', (e) => {
        speed = parseFloat(e.target.value);
    });
    elRole.addEventListener('change', (e) => {
        role = e.target.value;
        renderTimeline();
        log(`Role 変更：${role}`, '');
    });

    // ── 初期化 ─────────────────────────────────────────────────────────
    loadSample().then(() => {
        tick();
    });
})();
