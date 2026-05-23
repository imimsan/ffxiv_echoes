/* FFXIV Echoes — Replay Inspector controller
 *
 * lib/trace-player.js と lib/arena-svg.js を組み合わせて
 * trace.json を SVG で時間軸再生する。
 */
(function () {
    'use strict';

    const $ = (id) => document.getElementById(id);

    const elFileInput = $('file-input');
    const elDropZone = $('drop-zone');
    const elDropZoneText = $('drop-zone-text');
    const elMetaRow = $('meta-row');
    const elBtnPlay = $('btn-play');
    const elBtnPause = $('btn-pause');
    const elBtnPrev = $('btn-prev');
    const elBtnNext = $('btn-next');
    const elBtnReset = $('btn-reset');
    const elSeek = $('seek-bar');
    const elTimeNow = $('time-now');
    const elTimeTotal = $('time-total');
    const elSpeed = $('speed-select');
    const elActiveCount = $('active-count');
    const elActiveList = $('active-aoe-list');
    const elEventLog = $('event-log');
    const elSkipReasons = $('skip-reasons');
    const elArenaStage = $('arena-stage');

    const player = new window.TracePlayer();

    let currentSvg = null;
    let _seekActive = false;
    const escape = (s) => String(s == null ? '' : s).replace(/[&<>]/g, (c) =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

    // ── トレース読み込み ────────────────────────────────────────
    function loadTrace(trace, sourceLabel) {
        try {
            player.load(trace);
            renderArena();
            updateMeta(sourceLabel);
            elDropZoneText.innerHTML = `ロード済み: <b>${escape(sourceLabel || 'trace.json')}</b><br><span class="muted small">クリック or ドロップで差し替え</span>`;
        } catch (e) {
            console.error('failed to load trace', e);
            alert(`trace のロードに失敗: ${e.message}`);
        }
    }

    function updateMeta(sourceLabel) {
        const m = player.trace && player.trace.meta;
        const a = player.trace && player.trace.arena;
        if (!m && !a) {
            elMetaRow.innerHTML = '';
            return;
        }
        const parts = [];
        if (sourceLabel) parts.push(`<span class="meta-kv">source<b>${escape(sourceLabel)}</b></span>`);
        if (m && m.zone) parts.push(`<span class="meta-kv">zone<b>${escape(m.zone)}</b></span>`);
        if (m && m.boss) parts.push(`<span class="meta-kv">boss<b>${escape(m.boss)}</b></span>`);
        if (m && m.schema_version) parts.push(`<span class="meta-kv">schema<b>${escape(m.schema_version)}</b></span>`);
        if (a) parts.push(`<span class="meta-kv">arena<b>(${a.center_x.toFixed(1)},${a.center_z.toFixed(1)}) r=${a.radius_m}m ${a.shape}</b></span>`);
        parts.push(`<span class="meta-kv">events<b>${player.events.length}</b></span>`);
        elMetaRow.innerHTML = parts.join(' ');
    }

    function renderArena() {
        elArenaStage.innerHTML = '';
        if (!player.trace || !player.trace.arena) {
            elArenaStage.innerHTML = '<div class="muted small">arena が trace に含まれていません</div>';
            currentSvg = null;
            return;
        }
        currentSvg = window.ArenaSvg.renderArena(player.trace.arena, { viewBoxSize: 500 });
        elArenaStage.appendChild(currentSvg);
        // 初回 active AoE 描画
        redrawActiveAoes();
    }

    function redrawActiveAoes() {
        if (!currentSvg) return;
        const layer = window.ArenaSvg.getAoeLayer(currentSvg);
        if (!layer) return;
        // clear
        while (layer.firstChild) layer.removeChild(layer.firstChild);
        const arena = player.trace.arena;
        for (const [, entry] of player.activeAoes) {
            const node = window.ArenaSvg.renderAoe(entry.spec, arena, { viewBoxSize: 500 });
            layer.appendChild(node);
        }
    }

    // ── UI 更新 ──────────────────────────────────────────────
    player.onTick((state) => {
        // 時計
        elTimeNow.textContent = state.currentT.toFixed(2);
        elTimeTotal.textContent = `/ ${state.duration.toFixed(2)}s`;
        if (!_seekActive) {
            elSeek.max = state.duration.toFixed(2);
            elSeek.value = state.currentT.toFixed(2);
        }
        // play ボタン文字
        elBtnPlay.textContent = state.playing ? '▶ 再生中' : '▶';

        // active AoE 描画 + リスト
        redrawActiveAoes();
        elActiveCount.textContent = state.activeAoes.size;

        const rows = [];
        const arena = state.arena || { center_x: 0, center_z: 0 };
        for (const [id, entry] of state.activeAoes) {
            const a = entry.spec;
            const remaining = (entry.expiresAt - state.currentT).toFixed(1);
            const cls = a.anchor === 'actor'
                ? 'actor'
                : (a.service && a.service.toLowerCase().includes('predict') ? 'predict' : '');
            const xRel = (typeof a.x_world === 'number' ? a.x_world - arena.center_x
                : (typeof a.x_relative === 'number' ? a.x_relative : 0));
            const zRel = (typeof a.z_world === 'number' ? a.z_world - arena.center_z
                : (typeof a.z_relative === 'number' ? a.z_relative : 0));
            const geom = formatGeom(a);
            rows.push(`
                <div class="aoe-row ${cls}">
                    <div>
                        <div class="id">${escape(id)}</div>
                        <div class="meta">${escape(a.service || '?')} · ${escape(a.shape)} · (${xRel.toFixed(1)},${zRel.toFixed(1)}) ${geom}</div>
                    </div>
                    <div class="remaining">${remaining}s</div>
                </div>
            `);
        }
        elActiveList.innerHTML = rows.length ? rows.join('') : '<div class="muted small">active なし</div>';

        // イベントログ
        const evRows = state.recentEvents.map((ev) => {
            const future = ev.t > state.currentT;
            const cls = `ev-row ${future ? 'future' : ''}`;
            const kindCls = `kind ${ev.kind}`;
            let detail = '';
            switch (ev.kind) {
                case 'draw_aoe':
                    detail = `${ev.id || ''} ${ev.shape || ''} ${ev.label ? '"' + ev.label + '"' : ''}`;
                    break;
                case 'remove_aoe':
                    detail = `${ev.id || ''} ${ev.reason ? '(' + ev.reason + ')' : ''}`;
                    break;
                case 'aoe_skipped':
                    detail = `${ev.reason || ''}`;
                    break;
                case 'cast_start':
                case 'cast_complete':
                    detail = `${ev.cast_id || ''} ${ev.cast_name || ''} ${ev.source ? '← ' + ev.source : ''}`;
                    break;
                case 'object_appear':
                case 'object_disappear':
                    detail = `${ev.object_id || ''} ${ev.name || ''}`;
                    break;
                default:
                    detail = JSON.stringify(ev).slice(0, 80);
            }
            return `
                <div class="${cls}">
                    <span class="t">${ev.t.toFixed(2)}</span>
                    <span class="${kindCls}">${escape(ev.kind)}</span>
                    <span class="muted">${escape(ev.service || '')}</span>
                    <span>${escape(detail)}</span>
                </div>
            `;
        });
        elEventLog.innerHTML = evRows.length ? evRows.join('') : '<div class="muted small">直近 event なし</div>';

        // skip reasons
        const grouped = new Map();
        for (const [key, count] of state.skipReasonCounts) {
            const [service, reason] = key.split('::');
            if (!grouped.has(service)) grouped.set(service, []);
            grouped.get(service).push({ reason, count });
        }
        if (grouped.size === 0) {
            elSkipReasons.innerHTML = '<div class="empty">なし</div>';
        } else {
            const parts = [];
            for (const [service, list] of grouped) {
                parts.push(`<div class="service">${escape(service)}</div>`);
                for (const r of list) {
                    parts.push(`<div class="reason">${escape(r.reason)}<span class="count">×${r.count}</span></div>`);
                }
            }
            elSkipReasons.innerHTML = parts.join('');
        }
    });

    function formatGeom(a) {
        const parts = [];
        if (typeof a.radius_m === 'number') parts.push(`r=${a.radius_m}`);
        if (typeof a.inner_radius_m === 'number') parts.push(`ri=${a.inner_radius_m}`);
        if (typeof a.fan_deg === 'number') parts.push(`fan=${a.fan_deg}°`);
        if (typeof a.half_width_m === 'number') parts.push(`hw=${a.half_width_m}`);
        if (typeof a.length_m === 'number') parts.push(`L=${a.length_m}`);
        return parts.join(' ');
    }

    // ── ファイル入出力 ───────────────────────────────────────
    function handleFile(file) {
        if (!file) return;
        const reader = new FileReader();
        reader.onload = (e) => {
            try {
                const trace = JSON.parse(e.target.result);
                loadTrace(trace, file.name);
            } catch (err) {
                console.error('JSON parse failed', err);
                alert(`JSON のパースに失敗: ${err.message}`);
            }
        };
        reader.readAsText(file);
    }

    elFileInput.addEventListener('change', (e) => {
        const file = e.target.files && e.target.files[0];
        handleFile(file);
    });

    elDropZone.addEventListener('click', (e) => {
        if (e.target === elFileInput) return;
        elFileInput.click();
    });

    ['dragenter', 'dragover'].forEach((evName) => {
        elDropZone.addEventListener(evName, (e) => {
            e.preventDefault();
            e.stopPropagation();
            elDropZone.classList.add('dragover');
        });
    });
    ['dragleave', 'drop'].forEach((evName) => {
        elDropZone.addEventListener(evName, (e) => {
            e.preventDefault();
            e.stopPropagation();
            elDropZone.classList.remove('dragover');
        });
    });
    elDropZone.addEventListener('drop', (e) => {
        const file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
        handleFile(file);
    });

    // ── 制御ボタン ─────────────────────────────────────────
    elBtnPlay.addEventListener('click', () => player.play());
    elBtnPause.addEventListener('click', () => player.pause());
    elBtnPrev.addEventListener('click', () => player.step(-1));
    elBtnNext.addEventListener('click', () => player.step(+1));
    elBtnReset.addEventListener('click', () => { player.pause(); player.seek(0); });
    elSpeed.addEventListener('change', (e) => player.setSpeed(parseFloat(e.target.value)));

    elSeek.addEventListener('input', (e) => {
        _seekActive = true;
        player.pause();
        player.seek(parseFloat(e.target.value));
    });
    elSeek.addEventListener('change', () => {
        _seekActive = false;
    });

    // ── デフォルト sample 読み込み ────────────────────────────
    fetch('sample-trace.json')
        .then((res) => {
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            return res.json();
        })
        .then((trace) => loadTrace(trace, 'sample-trace.json'))
        .catch((err) => {
            console.warn('sample-trace.json の fetch 失敗:', err);
            elDropZoneText.innerHTML = `<span class="muted small">sample-trace.json の自動ロードに失敗。<br>file:// で開いている場合はローカル http サーバ経由で開いてください。<br>または手動でファイルをドロップしてください。</span>`;
        });
})();
