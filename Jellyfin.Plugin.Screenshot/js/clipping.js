/** Focus clip editor. All preview framing is CSS-only; exports retain the source DAR. */
(function () {
    'use strict';
    if (window.JellyfinClip) return;
    const TICKS = 10_000_000;
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
        try {
            await fetch(`${state.context.server}/Screenshot/clips/${encodeURIComponent(id)}`, {
                method: 'DELETE', headers: state.context.authorizationHeaders, keepalive: true
            });
        } catch (_) { /* Expiry cleanup handles disconnected clients. */ }
    }

    async function create(state, preview) {
        const context = state.context;
        const controller = new AbortController();
        state.request = controller;
        const response = await fetch(`${context.server}/Screenshot/clips`, {
            method: 'POST', signal: controller.signal,
            headers: { 'Content-Type': 'application/json', ...context.authorizationHeaders },
            body: JSON.stringify({
                ItemId: context.itemId, MediaSourceId: context.mediaSourceId,
                AnchorTicks: context.anchorTicks,
                BeforeTicks: Math.round((preview ? state.maxBefore : state.before) * TICKS),
                AfterTicks: Math.round((preview ? state.maxAfter : state.after) * TICKS),
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
        state.request = null;
        return result;
    }

    function status(state, message, error = false) {
        state.$('.jfc-status').textContent = message;
        state.$('.jfc-status').classList.toggle('jfc-error', error);
    }

    function update(state, seek = false) {
        const { $, anchor, maxBefore, maxAfter } = state;
        const width = maxBefore + maxAfter;
        const start = maxBefore - state.before;
        const end = maxBefore + state.after;
        const percent = seconds => `${width ? seconds / width * 100 : 0}%`;
        $('#jfc-before').value = Number(state.before.toFixed(3));
        $('#jfc-after').value = Number(state.after.toFixed(3));
        $('#jfc-before').max = maxBefore;
        $('#jfc-after').max = maxAfter;
        $('.jfc-start-time').textContent = `Starts at ${time(anchor - state.before)}`;
        $('.jfc-end-time').textContent = `Ends at ${time(anchor + state.after)}`;
        $('.jfc-duration').textContent = `${short(state.before + state.after)} selected`;
        $('.jfc-export-label').textContent = state.exporting ? 'Creating clip…' : `Create ${short(state.before + state.after)} clip`;
        $('.jfc-export').disabled = !state.ready || state.busy || state.before + state.after <= 0;
        $('.jfc-play').disabled = !state.ready || state.busy || state.before + state.after <= 0;
        $('.jfc-controls').disabled = state.busy || !state.ready;
        $('#jfc-subtitles').disabled = state.busy || !(Number.isInteger(state.context.subtitleStreamIndex) && state.context.subtitleStreamIndex >= 0);
        $('.jfc-left').style.width = percent(start);
        $('.jfc-right').style.width = percent(width - end);
        $('.jfc-selection').style.left = percent(start);
        $('.jfc-selection').style.width = percent(end - start);
        $('.jfc-anchor').style.left = percent(maxBefore);
        $('.jfc-anchor-label').style.left = percent(maxBefore);
        // Keep the anchor label in the box near the beginning/end of a file.
        $('.jfc-anchor-label').style.transform = maxBefore / width < .15 ? 'none' : maxBefore / width > .85 ? 'translateX(-100%)' : 'translateX(-50%)';
        $('.jfc-window-start').textContent = time(anchor - maxBefore);
        $('.jfc-window-end').textContent = time(anchor + maxAfter);
        for (const [selector, position, minimum, maximum, absolute] of [
            ['.jfc-start', start, 0, maxBefore, anchor - state.before],
            ['.jfc-end', end, maxBefore, width, anchor + state.after]
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
            el.setAttribute('aria-pressed', state.before === Math.min(before, maxBefore) && state.after === Math.min(after, maxAfter));
        });
        state.dialog.querySelectorAll('[data-jfc-adjust]').forEach(el => {
            const [side, change] = el.dataset.jfcAdjust.split(':');
            el.disabled = Number(change) < 0 ? state[side] <= 0 : state[side] >= (side === 'before' ? maxBefore : maxAfter);
        });
        if (seek && state.ready) {
            state.video.pause();
            state.video.currentTime = Math.max(0, anchor - state.before - state.preview.start);
        }
        clock(state);
    }

    function clock(state) {
        const absolute = state.preview ? state.preview.start + state.video.currentTime : state.anchor - state.before;
        state.$('.jfc-preview-time').textContent = time(absolute);
        const width = state.maxBefore + state.maxAfter;
        state.$('.jfc-playhead').style.left = `${Math.max(0, Math.min(100, (absolute - state.anchor + state.maxBefore) / width * 100))}%`;
        state.$('.jfc-preview').classList.toggle('jfc-playing', !state.video.paused);
        state.$('.jfc-play').innerHTML = icon(state.video.paused ? 'play_arrow' : 'pause');
        state.$('.jfc-play').setAttribute('aria-label', state.video.paused ? 'Preview selected clip' : 'Pause clip preview');
    }

    function setSelection(state, side, input) {
        const number = Number(input);
        state[side] = Math.min(side === 'before' ? state.maxBefore : state.maxAfter, Math.max(0, Number.isFinite(number) ? number : 0));
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
        state.video.pause();
        state.ready = false; state.busy = true;
        state.video.removeAttribute('src'); state.video.load();
        const previous = state.preview;
        state.preview = null;
        state.$('.jfc-loading').hidden = false;
        state.$('.jfc-spinner').hidden = false;
        state.$('.jfc-loading-label').textContent = 'Preparing your preview…';
        state.$('.jfc-retry').hidden = true;
        status(state, 'The first preview may take a little longer while the video and subtitles are prepared.');
        update(state);
        if (previous) await release(state, previous.id);
        if (current !== state) return;
        try {
            const preview = await create(state, true);
            state.preview = preview;
            state.maxBefore = Math.max(0, state.anchor - preview.start);
            state.maxAfter = Math.max(0, preview.end - state.anchor);
            state.before = Math.min(state.before, state.maxBefore);
            state.after = Math.min(state.after, state.maxAfter);
            state.video.src = clipUrl(state, preview.id);
            state.video.load();
        } catch (error) {
            if (current !== state || error.name === 'AbortError') return;
            failedPreview(state, error.message);
        }
    }

    function failedPreview(state, message) {
        state.busy = false; state.ready = false;
        state.$('.jfc-loading').hidden = false;
        state.$('.jfc-spinner').hidden = true;
        state.$('.jfc-loading-label').textContent = 'Preview unavailable';
        state.$('.jfc-retry').hidden = false;
        status(state, message, true);
        update(state);
    }

    async function exportClip(state) {
        if (state.busy || !state.ready || state.before + state.after <= 0) return;
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
                </div><div class="jfc-heading"><span>Choose up to 1 minute before and after <strong class="jfc-moment"></strong>.</span><span class="jfc-duration"></span></div>
                <fieldset class="jfc-controls" disabled aria-label="Clip selection"><div class="jfc-timeline-section">
                    <div class="jfc-scale"><span class="jfc-window-start"></span><span class="jfc-window-end"></span><span class="jfc-anchor-label">Your moment</span></div>
                    <div class="jfc-timeline"><div class="jfc-frames" aria-hidden="true"></div><div class="jfc-left"></div><div class="jfc-right"></div><div class="jfc-selection"></div><div class="jfc-anchor"></div><div class="jfc-playhead"></div>
                        <button class="jfc-handle jfc-start" role="slider" aria-label="Clip start"></button><button class="jfc-handle jfc-end" role="slider" aria-label="Clip end"></button></div>
                    <p class="jfc-hint">Drag the handles to trim. Click the filmstrip to scrub.</p></div>
                    <div class="jfc-fields">
                        <div class="jfc-field"><label for="jfc-before">Before this moment</label><div class="jfc-input-row"><button data-jfc-adjust="before:-5" aria-label="Remove 5 seconds before">−</button><input id="jfc-before" type="number" min="0" max="60" step="0.1"><span class="jfc-unit">sec</span><button data-jfc-adjust="before:5" aria-label="Add 5 seconds before">+</button></div><span class="jfc-boundary jfc-start-time"></span></div>
                        <div class="jfc-field"><label for="jfc-after">After this moment</label><div class="jfc-input-row"><button data-jfc-adjust="after:-5" aria-label="Remove 5 seconds after">−</button><input id="jfc-after" type="number" min="0" max="60" step="0.1"><span class="jfc-unit">sec</span><button data-jfc-adjust="after:5" aria-label="Add 5 seconds after">+</button></div><span class="jfc-boundary jfc-end-time"></span></div>
                    </div><div class="jfc-presets"><button data-jfc-preset="30,0">Last 30s</button><button data-jfc-preset="15,15">±15s</button><button data-jfc-preset="30,30">±30s</button><button data-jfc-preset="60,60">Full 2 min</button></div>
                </fieldset></div>
                <footer class="jfc-footer"><label class="jfc-subs"><input id="jfc-subtitles" type="checkbox">Include selected subtitles</label><div class="jfc-actions"><span class="jfc-format">MP4</span><button class="jfc-cancel">Cancel</button><button class="jfc-export" disabled>${icon('content_cut')}<span class="jfc-export-label"></span></button></div></footer><p class="jfc-status" role="status" aria-live="polite"></p>`;
            const maxBefore = Math.min(60, context.anchorTicks / TICKS);
            const maxAfter = Math.min(60, (context.runtimeTicks - context.anchorTicks) / TICKS);
            const state = { dialog, context, anchor: context.anchorTicks / TICKS, maxBefore, maxAfter,
                before: Math.min(30, maxBefore), after: Math.min(15, maxAfter), ready: false, busy: true,
                preview: null, download: null, request: null, focus: document.activeElement,
                $: selector => dialog.querySelector(selector), video: dialog.querySelector('video') };
            current = state;
            state.$('.jfc-name').textContent = context.name;
            state.$('.jfc-moment').textContent = time(state.anchor);
            state.$('.jfc-close').onclick = close; state.$('.jfc-cancel').onclick = close;
            dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
            dialog.addEventListener('click', event => { if (event.target === dialog) { const b = dialog.getBoundingClientRect(); if (event.clientX < b.left || event.clientX > b.right || event.clientY < b.top || event.clientY > b.bottom) close(); } });
            state.$('.jfc-retry').onclick = () => prepare(state);
            state.$('#jfc-subtitles').onchange = () => prepare(state);
            state.$('.jfc-export').onclick = () => exportClip(state);
            state.$('.jfc-play').onclick = async () => {
                if (!state.ready) return;
                if (!state.video.paused) state.video.pause();
                else {
                    const start = state.anchor - state.before - state.preview.start;
                    const end = state.anchor + state.after - state.preview.start;
                    if (state.video.currentTime < start || state.video.currentTime >= end - .04) state.video.currentTime = Math.max(0, start);
                    try { await state.video.play(); } catch (_) { status(state, 'The preview could not play. Try reloading it.', true); }
                }
            };
            state.$('.jfc-mute').onclick = () => { state.video.muted = !state.video.muted; state.$('.jfc-mute').innerHTML = icon(state.video.muted ? 'volume_off' : 'volume_up'); state.$('.jfc-mute').setAttribute('aria-label', state.video.muted ? 'Unmute clip preview' : 'Mute clip preview'); };
            state.video.addEventListener('loadedmetadata', () => {
                if (current !== state || !state.preview) return;
                state.busy = false; state.ready = true; state.$('.jfc-loading').hidden = true;
                status(state, ''); update(state, true); thumbnails(state, state.preview);
            });
            state.video.addEventListener('error', () => { if (current === state && state.preview) failedPreview(state, 'The preview could not load or has expired. Retry to prepare it again.'); });
            for (const event of ['play', 'pause', 'seeked', 'timeupdate']) state.video.addEventListener(event, () => {
                if (state.preview && !state.video.paused && state.video.currentTime >= state.anchor + state.after - state.preview.start) state.video.pause();
                clock(state);
            });
            for (const side of ['before', 'after']) state.$(`#jfc-${side}`).oninput = event => setSelection(state, side, event.target.value);
            dialog.querySelectorAll('[data-jfc-adjust]').forEach(el => el.onclick = () => { const [side, change] = el.dataset.jfcAdjust.split(':'); setSelection(state, side, state[side] + Number(change)); });
            dialog.querySelectorAll('[data-jfc-preset]').forEach(el => el.onclick = () => { const [before, after] = el.dataset.jfcPreset.split(',').map(Number); state.before = Math.min(before, state.maxBefore); state.after = Math.min(after, state.maxAfter); update(state, true); });
            for (const [selector, side] of [['.jfc-start', 'before'], ['.jfc-end', 'after']]) {
                const handle = state.$(selector); let dragging = false;
                const move = event => { const box = state.$('.jfc-timeline').getBoundingClientRect(); const position = (event.clientX - box.left) / box.width * (state.maxBefore + state.maxAfter); setSelection(state, side, side === 'before' ? state.maxBefore - position : position - state.maxBefore); };
                handle.onpointerdown = event => { event.preventDefault(); event.stopPropagation(); handle.focus(); handle.setPointerCapture(event.pointerId); dragging = true; move(event); };
                handle.onpointermove = event => { if (dragging) move(event); };
                handle.onpointerup = handle.onpointercancel = () => { dragging = false; };
                handle.onkeydown = event => {
                    const step = event.shiftKey ? 5 : 1;
                    let target = state[side];
                    if (event.key === 'ArrowLeft') target += side === 'before' ? step : -step;
                    else if (event.key === 'ArrowRight') target += side === 'before' ? -step : step;
                    else if (event.key === 'Home') target = side === 'before' ? state.maxBefore : 0;
                    else if (event.key === 'End') target = side === 'before' ? 0 : state.maxAfter;
                    else return;
                    event.preventDefault(); setSelection(state, side, target);
                };
            }
            state.$('.jfc-timeline').onpointerdown = event => {
                if (!state.ready || state.busy || event.target.closest('.jfc-handle')) return;
                const box = state.$('.jfc-timeline').getBoundingClientRect();
                const absolute = state.anchor - state.maxBefore + (event.clientX - box.left) / box.width * (state.maxBefore + state.maxAfter);
                state.video.pause(); state.video.currentTime = Math.max(0, Math.max(state.anchor - state.before, Math.min(state.anchor + state.after, absolute)) - state.preview.start);
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
