/* FFXIV Echoes — TracePlayer
 *
 * trace.json (docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md §3)
 * を時刻軸で再生する制御クラス。
 *
 * window.TracePlayer として公開（ES modules は使わない）。
 *
 * 使い方:
 *   const player = new TracePlayer();
 *   player.load(traceObj);
 *   player.onTick(state => { ... });
 *   player.play();
 *   player.seek(15.0);
 *   player.setSpeed(2);
 *   player.pause();
 *   player.step(+1);  // 次のイベントへ
 */
(function (global) {
    'use strict';

    class TracePlayer {
        constructor() {
            this.trace = null;
            this.events = [];      // ソート済み events
            this.activeAoes = new Map(); // id → { spec, drawnAt, expiresAt }
            this.skipReasonCounts = new Map(); // "service::reason" → count
            this.recentEvents = []; // 直近 ±5 秒で fire したもの

            this.currentT = 0;
            this.duration = 0;
            this.playing = false;
            this.speed = 1.0;
            this._rafId = null;
            this._lastRafTs = 0;
            this._listeners = [];
        }

        load(trace) {
            if (!trace || !Array.isArray(trace.events)) {
                throw new Error('trace.events array is required');
            }
            this.trace = trace;
            this.events = trace.events.slice().sort((a, b) => a.t - b.t);
            this.duration = this.events.length > 0
                ? Math.max(...this.events.map(e => {
                    if (e.kind === 'draw_aoe' && typeof e.duration_sec === 'number') {
                        return e.t + e.duration_sec;
                    }
                    return e.t;
                }))
                : 0;
            this.duration = Math.max(this.duration, 1);
            this.reset();
        }

        reset() {
            this.activeAoes.clear();
            this.skipReasonCounts.clear();
            this.recentEvents = [];
            this.currentT = 0;
            this._lastRafTs = 0;
            this._rebuildState(0);
            this._emit();
        }

        onTick(cb) {
            if (typeof cb === 'function') this._listeners.push(cb);
        }

        offTick(cb) {
            this._listeners = this._listeners.filter(fn => fn !== cb);
        }

        setSpeed(n) {
            this.speed = Math.max(0.1, Math.min(8, Number(n) || 1));
        }

        play() {
            if (this.playing) return;
            if (this.currentT >= this.duration) {
                this.currentT = 0;
                this._rebuildState(0);
            }
            this.playing = true;
            this._lastRafTs = performance.now();
            this._scheduleFrame();
        }

        pause() {
            this.playing = false;
            if (this._rafId !== null) {
                cancelAnimationFrame(this._rafId);
                this._rafId = null;
            }
        }

        seek(t) {
            const target = Math.max(0, Math.min(this.duration, Number(t) || 0));
            this.currentT = target;
            this._rebuildState(target);
            this._emit();
        }

        /**
         * dir: +1 = 次のイベントへ進む / -1 = 直前のイベントへ戻る
         */
        step(dir) {
            const d = dir < 0 ? -1 : 1;
            const t = this.currentT;
            let nextT;
            if (d > 0) {
                const next = this.events.find(e => e.t > t + 1e-6);
                nextT = next ? next.t : this.duration;
            } else {
                // 直近の過去 event
                const prev = [...this.events].reverse().find(e => e.t < t - 1e-6);
                nextT = prev ? prev.t : 0;
            }
            this.seek(nextT);
        }

        _scheduleFrame() {
            this._rafId = requestAnimationFrame((ts) => this._frame(ts));
        }

        _frame(ts) {
            if (!this.playing) return;
            const dt = (ts - this._lastRafTs) / 1000.0;
            this._lastRafTs = ts;
            const next = this.currentT + dt * this.speed;
            this._advanceTo(next);
            if (this.currentT >= this.duration) {
                this.playing = false;
                this._emit();
                return;
            }
            this._emit();
            this._scheduleFrame();
        }

        /** _rebuildState: 時刻 t における activeAoes / skipReasonCounts を最初から走査して構築する。 */
        _rebuildState(t) {
            this.activeAoes.clear();
            this.skipReasonCounts.clear();
            this.recentEvents = [];
            for (const ev of this.events) {
                if (ev.t > t) break;
                this._applyEvent(ev);
            }
            // 期限切れ remove
            this._pruneExpired(t);
            this._collectRecent(t);
        }

        /** _advanceTo(target): currentT → target に進める途中で発火するイベントを順次適用 */
        _advanceTo(target) {
            const from = this.currentT;
            const to = Math.min(target, this.duration);
            for (const ev of this.events) {
                if (ev.t > from && ev.t <= to) {
                    this._applyEvent(ev);
                }
            }
            this.currentT = to;
            this._pruneExpired(this.currentT);
            this._collectRecent(this.currentT);
        }

        _applyEvent(ev) {
            switch (ev.kind) {
                case 'draw_aoe': {
                    const id = ev.id || `aoe_${ev.t}`;
                    const expiresAt = typeof ev.duration_sec === 'number'
                        ? ev.t + ev.duration_sec
                        : ev.t + 5;
                    this.activeAoes.set(id, {
                        spec: ev,
                        drawnAt: ev.t,
                        expiresAt,
                    });
                    break;
                }
                case 'remove_aoe': {
                    if (ev.id) this.activeAoes.delete(ev.id);
                    break;
                }
                case 'aoe_skipped': {
                    const key = `${ev.service || 'unknown'}::${ev.reason || 'unspecified'}`;
                    this.skipReasonCounts.set(key, (this.skipReasonCounts.get(key) || 0) + 1);
                    break;
                }
                default:
                    // cast_start / cast_complete / object_appear などは Active には影響しない
                    break;
            }
        }

        _pruneExpired(t) {
            for (const [id, entry] of this.activeAoes.entries()) {
                if (entry.expiresAt < t) {
                    this.activeAoes.delete(id);
                }
            }
        }

        _collectRecent(t) {
            const window = 5.0;
            this.recentEvents = this.events
                .filter(ev => ev.t >= t - window && ev.t <= t + window)
                .slice(-50); // 上限
        }

        _emit() {
            const state = {
                currentT: this.currentT,
                duration: this.duration,
                playing: this.playing,
                speed: this.speed,
                activeAoes: this.activeAoes,
                recentEvents: this.recentEvents,
                skipReasonCounts: this.skipReasonCounts,
                arena: this.trace ? this.trace.arena : null,
                meta: this.trace ? this.trace.meta : null,
            };
            for (const fn of this._listeners) {
                try { fn(state); } catch (e) { console.error('TracePlayer listener error', e); }
            }
        }
    }

    global.TracePlayer = TracePlayer;
})(window);
