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
    const elOverlay = $('overlay-center');
    const elTimers = $('timer-bars');
    const elChat = $('chat-log');

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
                        addOverlayText(action.text, action.duration || 5, action.color, action.size);
                        break;
                    case 'timer_bar':
                        addTimerBar(action.label || '', action.duration || 0, action.color, action.warn_at);
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

    // ── オーバーレイ描画（テキスト + タイマーバー） ─────────────────────
    const renderOverlays = () => {
        const now = performance.now();

        // 期限切れを除去
        for (let i = activeOverlayTexts.length - 1; i >= 0; i--) {
            if (now > activeOverlayTexts[i].startedAt + activeOverlayTexts[i].duration) {
                activeOverlayTexts.splice(i, 1);
            }
        }
        for (let i = activeTimerBars.length - 1; i >= 0; i--) {
            if (now > activeTimerBars[i].startedAt + activeTimerBars[i].duration) {
                activeTimerBars.splice(i, 1);
            }
        }

        // テキスト
        elOverlay.innerHTML = '';
        activeOverlayTexts.forEach((item) => {
            const remaining = item.duration - (now - item.startedAt);
            const div = document.createElement('div');
            div.className = `text-item size-${item.size}`;
            div.style.color = item.color;
            if (remaining < 400) {
                div.classList.add('fading');
            }
            div.textContent = item.text;
            elOverlay.appendChild(div);
        });

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
