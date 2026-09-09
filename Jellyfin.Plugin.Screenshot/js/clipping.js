/** Focus clip editor. All preview framing is CSS-only; exports retain the source DAR. */
(function () {
    'use strict';
    if (window.JellyfinClip) return;
    const TICKS = 10_000_000;
    const RENDER_TIMEOUT = 610_000; // Allow the server's ten-minute render timeout to respond.
    const MEDIA_TIMEOUT = 30_000;
    let current = null;
    let generation = 0;
    let opening = false;
    const icon = name => `<span class="jfc-icon material-icons" aria-hidden="true">${name}</span>`;
    const time = seconds => {
        seconds = Math.max(0, Math.floor(seconds));
        const hours = Math.floor(seconds / 3600);
        return (hours ? `${hours}:` : '') + `${String(Math.floor(seconds / 60) % 60).padStart(hours ? 2 : 1, '0')}:${String(seconds % 60).padStart(2, '0')}`;
    };
    const short = seconds => `${Number(seconds.toFixed(1))}s`;
    const value = (object, name) => object[name] ?? object[name[0].toLowerCase() + name.slice(1)];

    function clipUrl(state, id, download = false) {
        const url = new URL(`${state.context.server}/Screenshot/clips/${encodeURIComponent(id)}`);
        // Video elements and native downloads cannot attach an Authorization header.
        url.searchParams.set('ApiKey', state.context.token);
        if (download) url.searchParams.set('download', 'true');
        return url.href;
    }

    async function release(state, id) {
        if (!id) return;
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 5_000);
        try {
            await fetch(`${state.context.server}/Screenshot/clips/${encodeURIComponent(id)}`, {
                method: 'DELETE', headers: state.context.authorizationHeaders, keepalive: true, signal: controller.signal
            });
        } catch (_) { /* Expiry cleanup handles disconnected clients. */ }
        finally { clearTimeout(timer); }
    }

    async function create(state, preview) {
        const context = state.context;
        const controller = new AbortController();
        state.request = controller;
        let timedOut = false;
        const timer = setTimeout(() => { timedOut = true; controller.abort(); }, RENDER_TIMEOUT);
        try {
            const response = await fetch(`${context.server}/Screenshot/clips`, {
                method: 'POST', signal: controller.signal,
                headers: { 'Content-Type': 'application/json', ...context.authorizationHeaders },
                body: JSON.stringify({
                    ItemId: context.itemId, MediaSourceId: context.mediaSourceId,
                    AnchorTicks: context.anchorTicks,
                    StartTicks: Math.round((preview ? state.windowStart : state.start) * TICKS),
                    EndTicks: Math.round((preview ? state.windowEnd : state.end) * TICKS),
                    AudioStreamIndex: context.audioStreamIndex,
                    SubtitleStreamIndex: state.$('#jfc-subtitles').checked ? context.subtitleStreamIndex : null,
                    Preview: preview
                })
            });
            if (!response.ok) {
                const body = await response.text();
                let message = body;
                try { const data = JSON.parse(body); message = typeof data === 'string' ? data : data.detail || data.title; } catch (_) { }
                throw new Error(message || `Clip request failed (${response.status}).`);
            }
            const data = await response.json();
            const result = {
                id: value(data, 'Id'), filename: value(data, 'Filename'),
                start: value(data, 'StartTicks') / TICKS, end: value(data, 'EndTicks') / TICKS
            };
            if (current !== state) { release(state, result.id); throw new DOMException('Closed', 'AbortError'); }
            return result;
        } catch (error) {
            if (timedOut) throw new Error('The server did not finish preparing the clip in time. Retry, or check the Jellyfin server log.');
            throw error;
        } finally {
            clearTimeout(timer);
            if (state.request === controller) state.request = null;
        }
    }

    function clearPreparationTimers(state) {
        clearInterval(state.preparationTimer);
        clearTimeout(state.mediaTimer);
    }

    function status(state, message, error = false) {
        state.$('.jfc-status').textContent = message;
        state.$('.jfc-status').classList.toggle('jfc-error', error);
    }

    function update(state, seek = false) {
        const { $, anchor, windowStart, windowEnd } = state;
        const width = windowEnd - windowStart;
        const start = state.start - windowStart;
        const end = state.end - windowStart;
        const duration = state.end - state.start;
        const percent = seconds => `${width ? seconds / width * 100 : 0}%`;
        $('#jfc-start').value = Number(state.start.toFixed(3));
        $('#jfc-duration').value = Number(duration.toFixed(3));
        $('#jfc-start').min = windowStart;
        $('#jfc-start').max = windowEnd;
        $('#jfc-duration').max = windowEnd - state.start;
        $('.jfc-start-time').textContent = `Starts at ${time(state.start)}`;
        $('.jfc-end-time').textContent = `Ends at ${time(state.end)}`;
        $('.jfc-duration').textContent = `${short(duration)} selected`;
        $('.jfc-export-label').textContent = state.exporting ? 'Creating clip…' : `Create ${short(duration)} clip`;
        $('.jfc-export').disabled = !state.ready || state.busy || duration <= 0;
        $('.jfc-play').disabled = !state.ready || state.busy || duration <= 0;
        $('.jfc-controls').disabled = state.busy || !state.ready;
        $('#jfc-subtitles').disabled = state.busy || !(Number.isInteger(state.context.subtitleStreamIndex) && state.context.subtitleStreamIndex >= 0);
        $('.jfc-left').style.width = percent(start);
        $('.jfc-right').style.width = percent(width - end);
        $('.jfc-selection').style.left = percent(start);
        $('.jfc-selection').style.width = percent(end - start);
        $('.jfc-anchor').style.left = percent(anchor - windowStart);
        $('.jfc-anchor-label').style.left = percent(anchor - windowStart);
        const anchorFraction = (anchor - windowStart) / width;
        $('.jfc-anchor-label').style.transform = anchorFraction < .15 ? 'none' : anchorFraction > .85 ? 'translateX(-100%)' : 'translateX(-50%)';
        $('.jfc-window-start').textContent = time(windowStart);
        $('.jfc-window-end').textContent = time(windowEnd);
        for (const [selector, position, minimum, maximum, absolute] of [
            ['.jfc-start', start, 0, end, state.start],
            ['.jfc-end', end, start, width, state.end]
        ]) {
            const el = $(selector);
            el.style.left = percent(position);
            el.setAttribute('aria-valuemin', minimum);
            el.setAttribute('aria-valuemax', maximum);
            el.setAttribute('aria-valuenow', position);
            el.setAttribute('aria-valuetext', time(absolute));
        }
        state.dialog.querySelectorAll('[data-jfc-preset]').forEach(el => {
            const [before, after] = el.dataset.jfcPreset.split(',').map(Number);
            el.setAttribute('aria-pressed', state.start === Math.max(windowStart, anchor - before) && state.end === Math.min(windowEnd, anchor + after));
        });
        state.dialog.querySelectorAll('[data-jfc-adjust]').forEach(el => {
            const [field, change] = el.dataset.jfcAdjust.split(':');
            const currentValue = field === 'start' ? state.start : duration;
            const minimum = field === 'start' ? windowStart : 0;
            const maximum = field === 'start' ? windowEnd : windowEnd - state.start;
            el.disabled = Number(change) < 0 ? currentValue <= minimum : currentValue >= maximum;
        });
        if (seek && state.ready) {
            state.video.pause();
            state.video.currentTime = Math.max(0, state.start - state.preview.start);
        }
        clock(state);
    }

    function clock(state) {
        const absolute = state.preview ? state.preview.start + state.video.currentTime : state.start;
        state.$('.jfc-preview-time').textContent = time(absolute);
        const width = state.windowEnd - state.windowStart;
        state.$('.jfc-playhead').style.left = `${Math.max(0, Math.min(100, (absolute - state.windowStart) / width * 100))}%`;
        const paused = state.video.paused;
        // Keep the hit target intact between pointerdown and click, even during timeupdate.
        const glyph = state.$('.jfc-play .jfc-icon');
        const name = paused ? 'play_arrow' : 'pause';
        if (glyph.textContent !== name) glyph.textContent = name;
        state.$('.jfc-play').setAttribute('aria-label', paused ? 'Preview selected clip' : 'Pause clip preview');
    }

    function setSelection(state, field, input) {
        const number = Number(input);
        if (!Number.isFinite(number)) return;
        if (field === 'start') {
            // Moving the starting point keeps the duration where the window allows it.
            const duration = state.end - state.start;
            state.start = Math.max(state.windowStart, Math.min(state.windowEnd, number));
            state.end = Math.min(state.windowEnd, state.start + duration);
        } else {
            state.end = state.start + Math.max(0, Math.min(state.windowEnd - state.start, number));
        }
        update(state, true);
    }

    function setEndpoint(state, side, value) {
        if (side === 'start') state.start = Math.max(state.windowStart, Math.min(state.end, value));
        else state.end = Math.max(state.start, Math.min(state.windowEnd, value));
        update(state, true);
    }

    async function thumbnails(state, preview) {
        const helper = document.createElement('video');
        helper.dataset.clipPreview = 'true';
        helper.muted = true;
        helper.crossOrigin = 'anonymous';
        helper.preload = 'auto';
        state.thumbnailVideo = helper;
        const wait = event => new Promise((resolve, reject) => {
            const timer = setTimeout(() => finish(new Error('Thumbnail unavailable')), 5000);
            const done = () => finish();
            const fail = () => finish(new Error('Thumbnail unavailable'));
            function finish(error) {
                clearTimeout(timer); helper.removeEventListener(event, done); helper.removeEventListener('error', fail);
                if (error) reject(error); else resolve();
            }
            helper.addEventListener(event, done, { once: true }); helper.addEventListener('error', fail, { once: true });
        });
        try {
            const loaded = wait('loadeddata');
            helper.src = clipUrl(state, preview.id);
            await loaded;
            const canvas = document.createElement('canvas');
            canvas.width = 160; canvas.height = Math.max(1, Math.round(160 * helper.videoHeight / helper.videoWidth));
            const context = canvas.getContext('2d');
            const frames = state.$('.jfc-frames');
            frames.replaceChildren();
            for (let index = 0; index < 8; index++) {
                if (current !== state || state.preview !== preview) return;
                const next = wait('seeked');
                helper.currentTime = Math.min(helper.duration - .05, (index + .5) / 8 * helper.duration);
                await next;
                if (current !== state || state.preview !== preview) return;
                context.drawImage(helper, 0, 0, canvas.width, canvas.height);
                const frame = new Image();
                frame.alt = ''; frame.draggable = false; frame.src = canvas.toDataURL('image/jpeg', .6); frames.append(frame);
            }
        } catch (_) { /* Trimming and playback remain available without a filmstrip. */ }
        finally { helper.removeAttribute('src'); helper.load(); }
    }

    async function prepare(state) {
        state.request?.abort();
        clearPreparationTimers(state);
        state.video.pause();
        state.ready = false; state.busy = true;
        state.video.removeAttribute('src'); state.video.load();
        const previous = state.preview;
        state.preview = null;
        state.$('.jfc-loading').hidden = false;
        state.$('.jfc-spinner').hidden = false;
        state.$('.jfc-loading-label').textContent = 'Preparing your preview…';
        state.$('.jfc-retry').hidden = true;
        const started = Date.now();
        state.preparationTimer = setInterval(() => {
            if (current !== state) return;
            const seconds = Math.floor((Date.now() - started) / 1000);
            state.$('.jfc-loading-label').textContent = `Preparing your preview… ${seconds}s`;
            if (seconds >= 15) status(state, 'The server is still rendering the preview. High-resolution video and subtitles can take longer. You can cancel at any time.');
        }, 1000);
        status(state, 'The server is rendering the available video around your moment.');
        update(state);
        if (previous) await release(state, previous.id);
        if (current !== state) return;
        try {
            const preview = await create(state, true);
            state.preview = preview;
            if (Math.abs(preview.start - state.windowStart) > .001 || Math.abs(preview.end - state.windowEnd) > .001) {
                throw new Error('The preview does not match the requested clipping window. Reload the client and try again.');
            }
            clearInterval(state.preparationTimer);
            state.$('.jfc-loading-label').textContent = 'Loading preview video…';
            status(state, 'The server finished preparing the preview. Loading it in this client…');
            state.mediaTimer = setTimeout(() => {
                if (current !== state || state.preview !== preview || state.ready) return;
                failedPreview(state, 'The preview was created, but this client could not load it within 30 seconds. Retry the preview or try Jellyfin in a web browser.');
                state.video.removeAttribute('src'); state.video.load();
            }, MEDIA_TIMEOUT);
            state.video.src = clipUrl(state, preview.id);
            state.video.load();
        } catch (error) {
            if (current !== state || error.name === 'AbortError') return;
            failedPreview(state, error.message);
        }
    }

    function failedPreview(state, message) {
        clearPreparationTimers(state);
        state.busy = false; state.ready = false;
        state.$('.jfc-loading').hidden = false;
        state.$('.jfc-spinner').hidden = true;
        state.$('.jfc-loading-label').textContent = 'Preview unavailable';
        state.$('.jfc-retry').hidden = false;
        status(state, message, true);
        update(state);
    }

    async function exportClip(state) {
        if (state.busy || !state.ready || state.end <= state.start) return;
        state.video.pause(); state.busy = true; state.exporting = true;
        status(state, 'Creating your clip… You can cancel by closing the editor.');
        update(state);
        try {
            if (state.download) await release(state, state.download.id);
            state.download = null;
            if (current !== state) return;
            const result = await create(state, false);
            state.download = result;
            const url = clipUrl(state, result.id, true);
            const link = document.createElement('a');
            link.href = url; link.download = result.filename; link.textContent = 'Download clip again'; link.className = 'jfc-download';
            status(state, 'Your clip is ready. ');
            state.$('.jfc-status').append(link);
            if (window.jmpNative?.startDownload) window.jmpNative.startDownload(url);
            else link.click();
        } catch (error) {
            if (current === state && error.name !== 'AbortError') status(state, error.message || 'Could not create the clip.', true);
        } finally {
            state.busy = false; state.exporting = false;
            if (current === state) update(state);
        }
    }

    function close() {
        generation++;
        opening = false;
        const state = current;
        current = null;
        if (!state) return;
        clearPreparationTimers(state);
        state.request?.abort();
        state.video.pause(); state.video.removeAttribute('src'); state.video.load();
        if (state.thumbnailVideo) { state.thumbnailVideo.removeAttribute('src'); state.thumbnailVideo.load(); }
        if (state.preview) release(state, state.preview.id);
        // Downloads are kept for 30 minutes so closing cannot race a native save dialog.
        state.dialog.close(); state.dialog.remove();
        document.body.classList.remove('jfc-open');
        if (state.focus?.isConnected) state.focus.focus({ preventScroll: true });
    }

    async function open() {
        if (current) { current.$('.jfc-close').focus(); return; }
        if (opening) return;
        opening = true;
        const ticket = ++generation;
        try {
            const context = await window.ScreenshotCaptureTools.getClipContext();
            if (ticket !== generation) return;
            let styles = document.getElementById('jfclip-styles');
            if (!styles) { styles = document.createElement('link'); styles.id = 'jfclip-styles'; styles.rel = 'stylesheet'; document.head.append(styles); }
            styles.href = `${context.server}/Screenshot/clipping.css`;
            const dialog = document.createElement('dialog');
            dialog.id = 'jfclip-editor'; dialog.setAttribute('aria-labelledby', 'jfc-title');
            dialog.innerHTML = `
                <header class="jfc-header"><div><div class="jfc-name"></div><h1 id="jfc-title">Create a clip</h1></div><button class="jfc-close" aria-label="Close clip editor">${icon('close')}</button></header>
                <div class="jfc-body"><div class="jfc-preview">
                    <video data-clip-preview playsinline preload="auto"></video>
                    <button class="jfc-play" aria-label="Preview selected clip" disabled>${icon('play_arrow')}</button>
                    <button class="jfc-mute" aria-label="Mute clip preview">${icon('volume_up')}</button><span class="jfc-preview-time"></span>
                    <div class="jfc-loading"><span class="jfc-spinner" aria-hidden="true"></span><span class="jfc-loading-label">Preparing your preview…</span><button class="jfc-retry" hidden>Retry preview</button></div>
                </div><div class="jfc-heading"><span>Trim anywhere in this window. Requested at <strong class="jfc-moment"></strong>.</span><span class="jfc-duration"></span></div>
                <fieldset class="jfc-controls" disabled aria-label="Clip selection"><div class="jfc-timeline-section">
                    <div class="jfc-scale"><span class="jfc-window-start"></span><span class="jfc-window-end"></span><span class="jfc-anchor-label">Your moment</span></div>
                    <div class="jfc-timeline"><div class="jfc-frames" aria-hidden="true"></div><div class="jfc-left"></div><div class="jfc-right"></div><div class="jfc-selection"></div><div class="jfc-anchor"></div><div class="jfc-playhead"></div>
                        <button class="jfc-handle jfc-start" role="slider" aria-label="Clip start"></button><button class="jfc-handle jfc-end" role="slider" aria-label="Clip end"></button></div>
                    <p class="jfc-hint">Drag the handles to trim. Click the filmstrip to scrub.</p></div>
                    <div class="jfc-fields">
                        <div class="jfc-field"><label for="jfc-start">Starting point</label><div class="jfc-input-row"><button data-jfc-adjust="start:-5" aria-label="Move start 5 seconds earlier">−</button><input id="jfc-start" type="number" min="0" max="60" step="0.1"><span class="jfc-unit">sec</span><button data-jfc-adjust="start:5" aria-label="Move start 5 seconds later">+</button></div><span class="jfc-boundary jfc-start-time"></span></div>
                        <div class="jfc-field"><label for="jfc-duration">Duration</label><div class="jfc-input-row"><button data-jfc-adjust="duration:-5" aria-label="Shorten duration by 5 seconds">−</button><input id="jfc-duration" type="number" min="0" max="60" step="0.1"><span class="jfc-unit">sec</span><button data-jfc-adjust="duration:5" aria-label="Extend duration by 5 seconds">+</button></div><span class="jfc-boundary jfc-end-time"></span></div>
                    </div><div class="jfc-presets"><button data-jfc-preset="30,0">Last 30s</button><button data-jfc-preset="15,15">±15s</button><button data-jfc-preset="30,30">±30s</button><button data-jfc-preset="60,60">Full window</button></div>
                </fieldset></div>
                <footer class="jfc-footer"><label class="jfc-subs"><input id="jfc-subtitles" type="checkbox">Include selected subtitles</label><div class="jfc-actions"><span class="jfc-format">MP4</span><button class="jfc-cancel">Cancel</button><button class="jfc-export" disabled>${icon('content_cut')}<span class="jfc-export-label"></span></button></div></footer><p class="jfc-status" role="status" aria-live="polite"></p>`;
            const maxBefore = Math.min(60, context.anchorTicks / TICKS);
            const maxAfter = Math.min(60, (context.runtimeTicks - context.anchorTicks) / TICKS);
            const anchor = context.anchorTicks / TICKS;
            const state = { dialog, context, anchor, windowStart: anchor - maxBefore, windowEnd: anchor + maxAfter,
                start: anchor - Math.min(30, maxBefore), end: anchor + Math.min(15, maxAfter), ready: false, busy: true,
                preview: null, download: null, request: null, focus: document.activeElement,
                $: selector => dialog.querySelector(selector), video: dialog.querySelector('video') };
            current = state;
            state.$('.jfc-name').textContent = context.name;
            state.$('.jfc-moment').textContent = time(state.anchor);
            state.$('.jfc-close').onclick = close; state.$('.jfc-cancel').onclick = close;
            dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
            dialog.addEventListener('click', event => { if (event.target === dialog) { const b = dialog.getBoundingClientRect(); if (event.clientX < b.left || event.clientX > b.right || event.clientY < b.top || event.clientY > b.bottom) close(); } });
            // Keep editor input out of the underlying player's global controls.
            for (const type of ['click', 'dblclick', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'keydown', 'keyup']) {
                dialog.addEventListener(type, event => event.stopPropagation());
            }
            state.$('.jfc-retry').onclick = () => prepare(state);
            state.$('#jfc-subtitles').onchange = () => prepare(state);
            state.$('.jfc-export').onclick = () => exportClip(state);
            state.$('.jfc-play').onclick = async () => {
                if (!state.ready || state.busy) return;
                if (!state.video.paused) state.video.pause();
                else {
                    const start = state.start - state.preview.start;
                    const end = state.end - state.preview.start;
                    if (state.video.currentTime < start || state.video.currentTime >= end - .04) state.video.currentTime = Math.max(0, start);
                    try { await state.video.play(); } catch (error) { if (current === state && error.name !== 'AbortError') status(state, 'The preview could not play. Try reloading it.', true); }
                }
            };
            state.$('.jfc-mute').onclick = () => { state.video.muted = !state.video.muted; state.$('.jfc-mute').innerHTML = icon(state.video.muted ? 'volume_off' : 'volume_up'); state.$('.jfc-mute').setAttribute('aria-label', state.video.muted ? 'Unmute clip preview' : 'Mute clip preview'); };
            state.video.addEventListener('loadedmetadata', () => {
                if (current !== state || !state.preview) return;
                clearPreparationTimers(state);
                state.busy = false; state.ready = true; state.$('.jfc-loading').hidden = true;
                status(state, ''); update(state, true); thumbnails(state, state.preview);
            });
            state.video.addEventListener('error', () => { if (current === state && state.preview) failedPreview(state, 'The preview could not load or has expired. Retry to prepare it again.'); });
            for (const event of ['play', 'pause', 'seeked', 'timeupdate']) state.video.addEventListener(event, () => {
                if (state.preview && !state.video.paused && state.video.currentTime >= state.end - state.preview.start) state.video.pause();
                clock(state);
            });
            for (const side of ['start', 'duration']) state.$(`#jfc-${side}`).oninput = event => setSelection(state, side, event.target.value);
            dialog.querySelectorAll('[data-jfc-adjust]').forEach(el => el.onclick = () => { const [side, change] = el.dataset.jfcAdjust.split(':'); setSelection(state, side, (side === 'duration' ? state.end - state.start : state.start) + Number(change)); });
            dialog.querySelectorAll('[data-jfc-preset]').forEach(el => el.onclick = () => { const [before, after] = el.dataset.jfcPreset.split(',').map(Number); state.start = Math.max(state.windowStart, state.anchor - before); state.end = Math.min(state.windowEnd, state.anchor + after); update(state, true); });
            for (const [selector, side] of [['.jfc-start', 'start'], ['.jfc-end', 'end']]) {
                const handle = state.$(selector); let dragging = false;
                const move = event => { const box = state.$('.jfc-timeline').getBoundingClientRect(); const position = state.windowStart + (event.clientX - box.left) / box.width * (state.windowEnd - state.windowStart); setEndpoint(state, side, position); };
                handle.onpointerdown = event => { if (!state.ready || state.busy || event.button !== 0) return; event.preventDefault(); event.stopPropagation(); handle.focus(); handle.setPointerCapture(event.pointerId); dragging = true; move(event); };
                handle.onpointermove = event => { if (dragging) move(event); };
                handle.onpointerup = handle.onpointercancel = event => { dragging = false; if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId); };
                handle.onlostpointercapture = () => { dragging = false; };
                handle.onkeydown = event => {
                    const step = event.shiftKey ? 5 : 1;
                    let target = state[side];
                    if (event.key === 'ArrowLeft') target -= step;
                    else if (event.key === 'ArrowRight') target += step;
                    else if (event.key === 'Home') target = state.windowStart;
                    else if (event.key === 'End') target = state.windowEnd;
                    else return;
                    event.preventDefault(); setEndpoint(state, side, target);
                };
            }
            state.$('.jfc-timeline').onpointerdown = event => {
                if (!state.ready || state.busy || event.target.closest('.jfc-handle')) return;
                const box = state.$('.jfc-timeline').getBoundingClientRect();
                const absolute = state.windowStart + (event.clientX - box.left) / box.width * (state.windowEnd - state.windowStart);
                state.video.pause(); state.video.currentTime = Math.max(0, Math.max(state.start, Math.min(state.end, absolute)) - state.preview.start);
            };
            (document.fullscreenElement || document.body).append(dialog);
            document.body.classList.add('jfc-open');
            dialog.showModal(); state.$('.jfc-close').focus();
            update(state); prepare(state);
        } catch (error) {
            if (ticket === generation) { close(); window.ScreenshotCaptureTools.showToast(error.message || 'Could not open the clip editor.'); }
        } finally { if (ticket === generation) opening = false; }
    }
    window.JellyfinClip = { open, close };
    window.addEventListener('pagehide', close);
})();
