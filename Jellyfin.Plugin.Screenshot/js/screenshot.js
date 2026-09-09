/**
 * Jellyfin Screenshot Capture — frontend script.
 * Injected into index.html by the plugin on startup.
 * Adds a camera button to the video OSD that captures the current frame via the server API.
 */
(function () {
    'use strict';

    if (window.ScreenshotCaptureTools) return;

    const LOG_PREFIX = '[ScreenshotCapture]';
    const BTN_ID = 'screenshot-capture-btn';
    const DIALOG_ID = 'screenshot-capture-dialog';
    const TICKS_PER_SECOND = 10_000_000;
    let closeCaptureDialog = null;

    /**
     * Loads one of Jellyfin Web's legacy modules without making capture depend on it.
     */
    function loadJellyfinModule(name) {
        return new Promise(resolve => {
            if (typeof window.require !== 'function') {
                resolve(null);
                return;
            }

            let settled = false;
            const finish = module => {
                if (settled) return;
                settled = true;
                clearTimeout(timeout);
                resolve(module);
            };
            const timeout = setTimeout(() => finish(null), 1_000);

            try {
                window.require([name], finish, error => {
                    console.debug(LOG_PREFIX, `Jellyfin module '${name}' unavailable:`, error);
                    finish(null);
                });
            } catch (error) {
                console.debug(LOG_PREFIX, `Could not load Jellyfin module '${name}':`, error);
                finish(null);
            }
        });
    }

    /**
     * Reads the Jellyfin item ID currently playing from the OSD DOM.
     * The native user-rating button carries a data-id attribute set by Jellyfin.
     */
    function getCurrentItemId() {
        return (
            document.querySelector('.videoOsdBottom .btnUserRating[data-id]')?.dataset?.id ||
            document.querySelector('.videoOsdBottom [data-id]')?.dataset?.id ||
            null
        );
    }

    /**
     * Injects the screenshot button before the native settings button in the OSD.
     */
    function addButton() {
        // The screenshot menu uses the capture theme even before the clip editor opens.
        if (!document.getElementById('jfclip-styles') && typeof ApiClient !== 'undefined') {
            const styles = document.createElement('link');
            styles.id = 'jfclip-styles';
            styles.rel = 'stylesheet';
            styles.href = `${ApiClient.serverAddress().replace(/\/$/, '')}/Screenshot/clipping.css`;
            document.head.append(styles);
        }
        addClipButton();
        if (document.getElementById(BTN_ID)) return;

        const controlsContainer = document.querySelector(
            '.videoOsdBottom .buttons.focuscontainer-x'
        );
        if (!controlsContainer) return;

        const settingsBtn = controlsContainer.querySelector('.btnVideoOsdSettings');
        if (!settingsBtn) return;

        const btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Take screenshot');
        btn.setAttribute('aria-haspopup', 'dialog');
        btn.setAttribute('aria-expanded', 'false');
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.className = 'autoSize paper-icon-button-light';
        btn.title = 'Screenshot';
        btn.innerHTML =
            '<span class="largePaperIconButton material-icons" aria-hidden="true">photo_camera</span>';

        btn.addEventListener('click', async function (e) {
            e.stopPropagation();
            e.preventDefault();

            const choice = await showCaptureOptions();
            if (choice) {
                captureScreenshot(choice === 'with-subtitles');
            }
        });

        settingsBtn.parentElement.insertBefore(btn, document.getElementById('screenshot-clip-btn') || settingsBtn);
        console.log(LOG_PREFIX, 'Button added to OSD.');
    }

    function addClipButton() {
        if (document.getElementById('screenshot-clip-btn')) return;
        const settings = document.querySelector('.videoOsdBottom .btnVideoOsdSettings');
        if (!settings) return;
        const button = document.createElement('button');
        button.id = 'screenshot-clip-btn';
        button.type = 'button';
        button.className = 'autoSize paper-icon-button-light';
        button.setAttribute('is', 'paper-icon-button-light');
        button.setAttribute('aria-label', 'Create a clip');
        button.title = 'Create a clip';
        button.innerHTML = '<span class="largePaperIconButton material-icons" aria-hidden="true">content_cut</span>';
        button.addEventListener('click', event => {
            event.preventDefault();
            event.stopPropagation();
            closeCaptureOptions();
            window.JellyfinClip?.open();
        });
        settings.before(button);
    }

    async function getClipContext() {
        const native = window._mpvVideoPlayerInstance;
        const itemId = native?._currentPlayOptions?.item?.Id || getCurrentItemId();
        if (!itemId) throw new Error('Could not identify the playing video.');
        // Pause before any network calls so the captured anchor cannot drift.
        const video = document.querySelector('video:not([data-clip-preview])');
        if (window.jmpNative && native?.pause) native.pause();
        else video?.pause();
        const session = await getCurrentSession(itemId);
        if (!session?.NowPlayingItem?.Id || session.NowPlayingItem.Id.toLowerCase() !== String(itemId).toLowerCase()) {
            throw new Error('The current playback session is unavailable. Try again in a moment.');
        }
        const anchorTicks = await getPositionTicks(itemId, session);
        const runtimeTicks = Number(session.NowPlayingItem.RunTimeTicks);
        if (!Number.isSafeInteger(runtimeTicks) || runtimeTicks <= 0
            || !Number.isSafeInteger(anchorTicks) || anchorTicks < 0 || anchorTicks > runtimeTicks) {
            throw new Error('Clipping requires a video with a known playback position and duration.');
        }
        return {
            itemId, anchorTicks, runtimeTicks, name: session.NowPlayingItem.Name || 'Video',
            mediaSourceId: session.PlayState?.MediaSourceId,
            audioStreamIndex: session.PlayState?.AudioStreamIndex,
            subtitleStreamIndex: session.PlayState?.SubtitleStreamIndex,
            server: ApiClient.serverAddress().replace(/\/$/, ''), token: ApiClient.accessToken(),
            authorizationHeaders: getAuthorizationHeaders()
        };
    }

    window.ScreenshotCaptureTools = { getClipContext, showToast };

    /**
     * Removes the screenshot button when leaving the video page.
     */
    function removeButton() {
        document.getElementById(BTN_ID)?.remove();
        document.getElementById('screenshot-clip-btn')?.remove();
        window.JellyfinClip?.close();
        closeCaptureOptions();
    }

    /**
     * Opens the themed screenshot menu and resolves with the selected capture mode.
     */
    function showCaptureOptions() {
        closeCaptureOptions();

        return new Promise(resolve => {
            const previouslyFocused = document.activeElement;
            const backdrop = document.createElement('div');
            backdrop.className = 'jfs-backdrop';
            backdrop.dataset.screenshotCaptureBackdrop = 'true';

            const container = document.createElement('div');
            container.id = DIALOG_ID;
            container.className = 'jfs-container';
            container.innerHTML = `
                <div class="jfs-panel" role="dialog" aria-modal="true" aria-labelledby="screenshot-capture-title">
                    <header class="jfs-header">
                        <div><div class="jfs-eyebrow">SCREENSHOT</div><h1 id="screenshot-capture-title">Take screenshot</h1></div>
                        <button type="button" class="jfs-close" data-capture-close aria-label="Close screenshot menu"><span class="material-icons" aria-hidden="true">close</span></button>
                    </header>
                    <div class="jfs-options">
                        <button type="button" class="jfs-option" data-capture-mode="with-subtitles">
                            <span class="jfs-option-icon material-icons" aria-hidden="true">closed_caption</span>
                            <span class="jfs-option-copy"><span class="jfs-option-title">With subtitles</span><span class="jfs-option-description">Include the selected subtitle track.</span></span>
                            <span class="jfs-option-arrow material-icons" aria-hidden="true">arrow_forward</span>
                        </button>
                        <button type="button" class="jfs-option" data-capture-mode="without-subtitles">
                            <span class="jfs-option-icon material-icons" aria-hidden="true">photo_camera</span>
                            <span class="jfs-option-copy"><span class="jfs-option-title">Without subtitles</span><span class="jfs-option-description">Capture the video image only.</span></span>
                            <span class="jfs-option-arrow material-icons" aria-hidden="true">arrow_forward</span>
                        </button>
                    </div>
                    <footer class="jfs-footer"><span class="jfs-format">JPEG</span><button type="button" class="jfs-cancel" data-capture-close>Cancel</button></footer>
                </div>`;
            document.getElementById(BTN_ID)?.setAttribute('aria-expanded', 'true');

            let settled = false;
            const finish = choice => {
                if (settled) return;
                settled = true;
                document.removeEventListener('keydown', onKeyDown);
                container.remove();
                backdrop.remove();
                closeCaptureDialog = null;
                document.getElementById(BTN_ID)?.setAttribute('aria-expanded', 'false');
                previouslyFocused?.focus?.();
                resolve(choice);
            };
            const onKeyDown = event => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    event.stopPropagation();
                    finish(null);
                } else if (event.key === 'Tab') {
                    const buttons = Array.from(container.querySelectorAll('button'));
                    const index = buttons.indexOf(document.activeElement);
                    if (event.shiftKey && index <= 0) {
                        event.preventDefault(); buttons[buttons.length - 1].focus();
                    } else if (!event.shiftKey && (index < 0 || index === buttons.length - 1)) {
                        event.preventDefault(); buttons[0].focus();
                    }
                }
            };
            closeCaptureDialog = () => finish(null);

            container.addEventListener('click', event => {
                event.stopPropagation();
                if (event.target === container || event.target.closest('[data-capture-close]')) {
                    finish(null);
                    return;
                }

                const option = event.target.closest('[data-capture-mode]');
                if (option) {
                    finish(option.dataset.captureMode);
                }
            });
            backdrop.addEventListener('click', () => finish(null));
            document.addEventListener('keydown', onKeyDown);

            (document.fullscreenElement || document.body).append(backdrop, container);
            requestAnimationFrame(() => {
                container.querySelector('[data-capture-mode]')?.focus();
            });
        });
    }

    function closeCaptureOptions() {
        if (closeCaptureDialog) {
            closeCaptureDialog();
            return;
        }

        document.getElementById(DIALOG_ID)?.remove();
        document.querySelector('[data-screenshot-capture-backdrop]')?.remove();
    }

    // MediaBrowser authorization is supported with legacy authorization disabled on
    // both Jellyfin 10.11 and 12. Use the signed-in client's token and device identity.
    function getAuthorizationHeaders() {
        const token = ApiClient.accessToken();
        if (!token) throw new Error('Please sign in to Jellyfin again to capture media.');
        const quote = value => JSON.stringify(String(value));
        return { Authorization: `MediaBrowser Token=${quote(token)}, DeviceId=${quote(ApiClient.deviceId())}` };
    }

    /**
     * Returns this client's active Jellyfin playback session.
     */
    async function getCurrentSession(itemId) {
        try {
            const serverAddress = ApiClient.serverAddress().replace(/\/$/, '');
            const deviceId = ApiClient.deviceId();
            const sessionsUrl = `${serverAddress}/Sessions?DeviceId=${encodeURIComponent(deviceId)}`;
            const res = await fetch(sessionsUrl, { headers: getAuthorizationHeaders() });

            if (!res.ok) {
                console.warn(LOG_PREFIX, 'Sessions API returned', res.status);
                return null;
            }

            const sessions = await res.json();
            return sessions.find(session =>
                session.DeviceId === deviceId && session.NowPlayingItem?.Id === itemId
            ) || sessions.find(session => session.DeviceId === deviceId) || null;
        } catch (err) {
            console.warn(LOG_PREFIX, 'Sessions API request failed:', err);
            return null;
        }
    }

    /**
     * Returns the current playback position in Jellyfin ticks (1 tick = 100 ns).
     *
     * Jellyfin's playback manager combines the player clock with any transcode/seek
     * offset. Session state and raw HTML video time are progressively less accurate
     * fallbacks for clients that do not expose the playback manager module.
     */
    async function getPositionTicks(itemId, session) {
        // jellyfin-desktop renders through MPV rather than an HTML video element.
        // Its player clock is exposed in milliseconds and is the freshest source
        // available; session reports can lag and a retained OSD slider can be stale.
        try {
            const nativePlayer = window._mpvVideoPlayerInstance;
            const nativeItemId = nativePlayer?._currentPlayOptions?.item?.Id;
            const isRequestedItem = !nativeItemId
                || String(nativeItemId).toLowerCase() === String(itemId).toLowerCase();
            const positionMs = nativePlayer?.currentTime?.();

            if (window.jmpNative
                && isRequestedItem
                && Number.isFinite(positionMs)
                && positionMs >= 0) {
                const ticks = Math.round(positionMs * 10_000);
                console.log(LOG_PREFIX, `Position from desktop player: ${ticks} ticks`);
                return ticks;
            }
        } catch (error) {
            console.warn(LOG_PREFIX, 'Could not read position from desktop player:', error);
        }

        const playbackModule = await loadJellyfinModule('playbackManager');
        const playbackManager = playbackModule?.playbackManager
            || playbackModule?.default
            || playbackModule;

        try {
            const player = playbackManager?.getCurrentPlayer?.();
            const currentItem = player ? playbackManager?.currentItem?.(player) : null;
            const isRequestedItem = !currentItem?.Id || currentItem.Id === itemId;

            if (player && isRequestedItem && typeof playbackManager?.getCurrentTicks === 'function') {
                // Jellyfin adds the current transcode/seek offset here. Raw video.currentTime
                // is only relative to the current stream and can be wildly wrong after seeking.
                const ticks = Math.round(playbackManager.getCurrentTicks(player));
                if (Number.isSafeInteger(ticks) && ticks >= 0) {
                    console.log(LOG_PREFIX, `Position from playback manager: ${ticks} ticks`);
                    return ticks;
                }
            }
        } catch (error) {
            console.warn(LOG_PREFIX, 'Could not read position from playback manager:', error);
        }

        const slider = document.querySelector('.videoOsdBottom .osdPositionSlider');
        const runtimeTicks = Number(session?.NowPlayingItem?.RunTimeTicks);
        const progressPercent = Number(slider?.value);
        if (slider
            && Number.isSafeInteger(runtimeTicks)
            && runtimeTicks > 0
            && Number.isFinite(progressPercent)
            && progressPercent >= 0
            && progressPercent <= 100) {
            const ticks = Math.round(runtimeTicks * progressPercent / 100);
            console.log(LOG_PREFIX, `Position from OSD progress: ${ticks} ticks`);
            return ticks;
        }

        const sessionTicks = Number(session?.PlayState?.PositionTicks);
        if (Number.isSafeInteger(sessionTicks) && sessionTicks >= 0) {
            console.log(LOG_PREFIX, `Position from Sessions API: ${sessionTicks} ticks`);
            return sessionTicks;
        }

        // Last-resort browser fallback for clients that expose neither module nor session state.
        const video = document.querySelector('video');
        const ticks = Math.round((video?.currentTime || 0) * TICKS_PER_SECOND);
        console.warn(LOG_PREFIX, `Using raw video position as final fallback: ${ticks} ticks`);
        return ticks;
    }

    function padNumber(value) {
        return String(value).padStart(2, '0');
    }

    function sanitizeFilenamePart(value) {
        return String(value || 'Screenshot')
            .replace(/[<>:"/\\|?*\u0000-\u001F]/g, '_')
            .replace(/[. ]+$/g, '')
            || 'Screenshot';
    }

    function buildFilename(item, positionTicks) {
        const name = sanitizeFilenamePart(item?.Name);
        const season = Number(item?.ParentIndexNumber);
        const episode = Number(item?.IndexNumber);
        const episodeCode = item?.Type === 'Episode'
            && item?.ParentIndexNumber != null
            && item?.IndexNumber != null
            && Number.isInteger(season)
            && Number.isInteger(episode)
            ? `-S${padNumber(season)}E${padNumber(episode)}`
            : '';
        const totalSeconds = Math.max(0, Math.floor(positionTicks / TICKS_PER_SECOND));
        const hours = Math.floor(totalSeconds / 3_600);
        const minutes = Math.floor(totalSeconds % 3_600 / 60);
        const seconds = totalSeconds % 60;

        return `${name}${episodeCode}-${padNumber(hours)}-${padNumber(minutes)}-${padNumber(seconds)}.jpg`;
    }

    function showFallbackToast(message, duration = 5_000) {
        document.getElementById('screenshot-capture-toast')?.remove();

        const toast = document.createElement('div');
        toast.id = 'screenshot-capture-toast';
        toast.className = 'toast';
        toast.setAttribute('role', 'status');
        toast.setAttribute('aria-live', 'polite');
        toast.textContent = message;
        toast.style.cssText = [
            'position:fixed',
            'left:50%',
            'bottom:7em',
            'transform:translateX(-50%)',
            'z-index:100000',
            'max-width:min(90vw,42em)',
            'padding:.9em 1.25em',
            'border-radius:.3em',
            'background:rgba(32,32,32,.96)',
            'color:#fff',
            'box-shadow:0 .15em .6em rgba(0,0,0,.35)',
            'font-size:1rem',
            'text-align:center',
            'overflow-wrap:anywhere',
            'white-space:pre-wrap'
        ].join(';');
        document.body.appendChild(toast);
        setTimeout(() => toast.remove(), duration);
    }

    async function showToast(message) {
        const toastModule = await loadJellyfinModule('toast');
        const jellyfinToast = toastModule?.default || toastModule;

        try {
            if (typeof jellyfinToast === 'function') {
                jellyfinToast(message);
                return;
            }
            if (typeof jellyfinToast?.show === 'function') {
                jellyfinToast.show(message);
                return;
            }
        } catch (error) {
            console.debug(LOG_PREFIX, 'Jellyfin toast failed:', error);
        }

        showFallbackToast(message);
    }

    function downloadWithResult(url) {
        return new Promise((resolve, reject) => {
            const requestId = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
            // Distinguish simultaneous captures of the same frame in the native handler.
            const downloadUrl = new URL(url);
            downloadUrl.searchParams.set('CaptureRequestId', requestId);
            const finish = result => {
                clearTimeout(timer);
                window.removeEventListener('jellyfin-download-result', onResult);
                window.removeEventListener('pagehide', onPageHide);
                resolve(result);
            };
            const onResult = event => {
                const result = event.detail;
                if (result?.requestId === requestId && ['complete', 'cancelled', 'failed'].includes(result.status)) finish(result);
            };
            const onPageHide = () => finish({ status: 'dismissed' });
            const timer = setTimeout(() => finish({ status: 'unknown' }), 15 * 60_000);
            window.addEventListener('jellyfin-download-result', onResult);
            window.addEventListener('pagehide', onPageHide, { once: true });
            try { window.jmpNative.startDownloadWithResult(downloadUrl.href, requestId); }
            catch (error) {
                clearTimeout(timer);
                window.removeEventListener('jellyfin-download-result', onResult);
                window.removeEventListener('pagehide', onPageHide);
                reject(error);
            }
        });
    }

    /**
     * Triggers a screenshot download.
     *
     * jellyfin-desktop (CEF): uses jmpNative.startDownload → CefBrowserHost::StartDownload.
     *   No frame navigation, so canceling the save dialog does not corrupt CEF load state.
     *
     * Browser / desktop fallback: fetches the complete image, then downloads a blob.
     *   Waiting for the response prevents DOM cleanup from aborting long subtitle renders.
     */
    async function captureScreenshot(includeSubtitles) {
        console.log(LOG_PREFIX, '--- captureScreenshot start ---');

        const itemId = getCurrentItemId();
        console.log(LOG_PREFIX, 'itemId:', itemId);
        if (!itemId) {
            console.warn(LOG_PREFIX, 'Could not determine item ID from OSD.');
            return;
        }

        const session = await getCurrentSession(itemId);
        const positionTicks = await getPositionTicks(itemId, session);
        console.log(LOG_PREFIX, 'positionTicks resolved:', positionTicks);
        const filename = buildFilename(session?.NowPlayingItem, positionTicks);

        const subtitleStreamIndex = session?.PlayState?.SubtitleStreamIndex;
        if (includeSubtitles && !(Number.isInteger(subtitleStreamIndex) && subtitleStreamIndex >= 0)) {
            const message = 'No subtitle track is currently selected.';
            console.warn(LOG_PREFIX, message);
            if (window.Dashboard?.alert) {
                window.Dashboard.alert(message);
            } else {
                window.alert(message);
            }
            return;
        }

        const serverAddress = ApiClient.serverAddress().replace(/\/$/, '');
        const apiKey = ApiClient.accessToken();

        const url = `${serverAddress}/Screenshot/capture`
            + `?itemId=${encodeURIComponent(itemId)}`
            + `&positionTicks=${positionTicks}`
            + (session?.PlayState?.MediaSourceId
                ? `&mediaSourceId=${encodeURIComponent(session.PlayState.MediaSourceId)}`
                : '')
            + (includeSubtitles
                ? `&subtitleStreamIndex=${subtitleStreamIndex}`
                : '')
            + `&ApiKey=${encodeURIComponent(apiKey)}`;

        console.log(LOG_PREFIX, 'Download URL (key redacted):', url.replace(apiKey, '[REDACTED]'));

        try {
            if (typeof window.jmpNative?.startDownloadWithResult === 'function') {
                await showToast(`Preparing screenshot: ${filename}`);
                const result = await downloadWithResult(url);
                if (result.status === 'complete') {
                    if (typeof result.fullPath === 'string' && result.fullPath.length > 0) {
                        // Keep long paths readable, including spaces, backslashes and Unicode.
                        showFallbackToast(`Screenshot saved to:\n${result.fullPath}`, 10_000);
                    } else {
                        showToast('Screenshot saved. This client did not report the saved location.');
                    }
                } else if (result.status === 'cancelled') {
                    showToast('Screenshot save cancelled.');
                } else if (result.status === 'failed') {
                    showToast('Screenshot could not be saved. Check the destination and try again.');
                } else if (result.status === 'unknown') {
                    showToast('Screenshot save status is unavailable. Check your downloads.');
                }
            } else if (window.jmpNative && window.jmpNative.startDownload) {
                // CEF desktop client — use native download API, no frame involved
                window.jmpNative.startDownload(url);
                showToast(`Screenshot download started: ${filename}. This Desktop version does not report the saved path.`);
                console.log(LOG_PREFIX, '✓ startDownload called (CEF path)');
            } else {
                showToast(`Creating screenshot as ${filename}`);

                const response = await fetch(url);
                if (!response.ok) {
                    const detail = await response.text();
                    throw new Error(detail || `Screenshot request failed (${response.status})`);
                }

                const blobUrl = URL.createObjectURL(await response.blob());
                const link = document.createElement('a');
                link.href = blobUrl;
                link.download = filename;
                link.style.display = 'none';
                document.body.appendChild(link);
                link.click();
                link.remove();
                setTimeout(() => URL.revokeObjectURL(blobUrl), 60_000);

                showToast(`Screenshot download started: ${filename}. Check your browser’s downloads for the saved location.`);
                console.log(LOG_PREFIX, '✓ screenshot response downloaded (browser path)');
            }
        } catch (err) {
            console.error(LOG_PREFIX, 'Capture failed:', err);
            showToast(`Screenshot failed: ${err.message || err}`);
        }
    }

    // ---------- Page observer ----------

    let lastHash = '';

    const observer = new MutationObserver(function () {
        const hash = window.location.hash;

        if (hash === lastHash) return;
        lastHash = hash;

        if (hash.startsWith('#/video')) {
            // OSD may not exist yet — watch for it
            startOsdWatch();
        } else {
            stopOsdWatch();
            removeButton();
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });

    // Also react to hash changes triggered by pushState
    window.addEventListener('hashchange', function () {
        const hash = window.location.hash;
        if (hash.startsWith('#/video')) {
            startOsdWatch();
        } else {
            stopOsdWatch();
            removeButton();
        }
    });

    // ---------- OSD watch ----------

    let osdObserver = null;

    function startOsdWatch() {
        if (osdObserver) return;
        osdObserver = new MutationObserver(function () {
            if (window.location.hash.startsWith('#/video')) {
                addButton();
            }
        });
        osdObserver.observe(document.body, { childList: true, subtree: true });
        // Try immediately in case OSD already exists
        addButton();
    }

    function stopOsdWatch() {
        if (osdObserver) {
            osdObserver.disconnect();
            osdObserver = null;
        }
    }

    // Handle initial load if already on video page
    if (window.location.hash.startsWith('#/video')) {
        startOsdWatch();
    }

    console.log(LOG_PREFIX, 'Loaded.');
})();
