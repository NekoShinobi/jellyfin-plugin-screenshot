/**
 * Jellyfin Screenshot Capture — frontend script.
 * Injected into index.html by the plugin on startup.
 * Adds a camera button to the video OSD that captures the current frame via the server API.
 */
(function () {
    'use strict';

    const LOG_PREFIX = '[ScreenshotCapture]';
    const BTN_ID = 'screenshot-capture-btn';
    const DIALOG_ID = 'screenshot-capture-dialog';
    let closeCaptureDialog = null;

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
        if (document.getElementById(BTN_ID)) return;

        const controlsContainer = document.querySelector(
            '.videoOsdBottom .buttons.focuscontainer-x'
        );
        if (!controlsContainer) return;

        const settingsBtn = controlsContainer.querySelector('.btnVideoOsdSettings');
        if (!settingsBtn) return;

        const btn = document.createElement('button');
        btn.id = BTN_ID;
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

        settingsBtn.parentElement.insertBefore(btn, settingsBtn);
        console.log(LOG_PREFIX, 'Button added to OSD.');
    }

    /**
     * Removes the screenshot button when leaving the video page.
     */
    function removeButton() {
        document.getElementById(BTN_ID)?.remove();
        closeCaptureOptions();
    }

    /**
     * Opens a Jellyfin-style action sheet and resolves with the selected capture mode.
     */
    function showCaptureOptions() {
        closeCaptureOptions();

        return new Promise(resolve => {
            const previouslyFocused = document.activeElement;
            const backdrop = document.createElement('div');
            backdrop.className = 'dialogBackdrop dialogBackdropOpened';
            backdrop.dataset.screenshotCaptureBackdrop = 'true';

            const container = document.createElement('div');
            container.id = DIALOG_ID;
            container.className = 'dialogContainer';
            container.innerHTML = `
                <div class="focuscontainer dialog actionsheet-not-fullscreen actionSheet centeredDialog opened"
                     role="dialog" aria-modal="true" aria-labelledby="screenshot-capture-title">
                    <div class="actionSheetContent">
                        <h1 id="screenshot-capture-title" class="actionSheetTitle">Take screenshot</h1>
                        <div class="actionSheetScroller scrollY">
                            <button is="emby-button" type="button"
                                    class="listItem listItem-button actionSheetMenuItem emby-button"
                                    data-capture-mode="with-subtitles">
                                <span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons closed_caption"
                                      aria-hidden="true"></span>
                                <div class="listItemBody actionsheetListItemBody">
                                    <div class="listItemBodyText actionSheetItemText">With subtitles</div>
                                </div>
                            </button>
                            <button is="emby-button" type="button"
                                    class="listItem listItem-button actionSheetMenuItem emby-button"
                                    data-capture-mode="without-subtitles">
                                <span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons photo_camera"
                                      aria-hidden="true"></span>
                                <div class="listItemBody actionsheetListItemBody">
                                    <div class="listItemBodyText actionSheetItemText">Without subtitles</div>
                                </div>
                            </button>
                        </div>
                    </div>
                </div>`;

            let settled = false;
            const finish = choice => {
                if (settled) return;
                settled = true;
                document.removeEventListener('keydown', onKeyDown);
                container.remove();
                backdrop.remove();
                closeCaptureDialog = null;
                previouslyFocused?.focus?.();
                resolve(choice);
            };
            const onKeyDown = event => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    finish(null);
                }
            };
            closeCaptureDialog = () => finish(null);

            container.addEventListener('click', event => {
                if (event.target === container) {
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

            document.body.append(backdrop, container);
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

    /**
     * Returns this client's active Jellyfin playback session.
     */
    async function getCurrentSession(itemId) {
        try {
            const serverAddress = ApiClient.serverAddress().replace(/\/$/, '');
            const apiKey = ApiClient.accessToken();
            const sessionsUrl = `${serverAddress}/Sessions?api_key=${encodeURIComponent(apiKey)}`;
            const res = await fetch(sessionsUrl);

            if (!res.ok) {
                console.warn(LOG_PREFIX, 'Sessions API returned', res.status);
                return null;
            }

            const sessions = await res.json();
            const deviceId = ApiClient.deviceId();
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
     * In the browser the HTML5 <video> element carries the real position.
     * In Jellyfin Media Player (JMP) the video is decoded by libmpv — the <video>
     * element exists but its currentTime stays at 0. We fall back to the Sessions
     * API which always reflects the authoritative server-side position.
     */
    async function getPositionTicks(session) {
        const video = document.querySelector('video');
        console.debug(LOG_PREFIX, 'video element:', video, 'currentTime:', video?.currentTime);

        if (video && video.currentTime > 0) {
            const ticks = Math.round(video.currentTime * 10_000_000);
            console.log(LOG_PREFIX, `Position from video element: ${video.currentTime}s → ${ticks} ticks`);
            return ticks;
        }

        console.log(LOG_PREFIX, 'video.currentTime unavailable, falling back to Sessions API');

        // Native / desktop client fallback: use the authoritative session position.
        const ticks = session?.PlayState?.PositionTicks ?? 0;
        console.log(LOG_PREFIX, `Position from Sessions API: ${ticks} ticks (session found: ${!!session})`);
        return ticks;
    }

    /**
     * Triggers a screenshot download.
     *
     * jellyfin-desktop (CEF): uses jmpNative.startDownload → CefBrowserHost::StartDownload.
     *   No frame navigation, so canceling the save dialog does not corrupt CEF load state.
     *
     * Browser / Qt WebEngine fallback: hidden iframe pointing at the API URL.
     *   Content-Disposition: attachment is intercepted by the browser's download handler
     *   without navigating the SPA.
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
        const positionTicks = await getPositionTicks(session);
        console.log(LOG_PREFIX, 'positionTicks resolved:', positionTicks);

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
            + `&api_key=${encodeURIComponent(apiKey)}`;

        console.log(LOG_PREFIX, 'Download URL (key redacted):', url.replace(apiKey, '[REDACTED]'));

        try {
            if (window.jmpNative && window.jmpNative.startDownload) {
                // CEF desktop client — use native download API, no frame involved
                window.jmpNative.startDownload(url);
                console.log(LOG_PREFIX, '✓ startDownload called (CEF path)');
            } else {
                // Browser / Qt WebEngine — hidden iframe triggers Content-Disposition handler
                const iframe = document.createElement('iframe');
                iframe.style.cssText = 'display:none;width:0;height:0;border:0;position:absolute;';
                iframe.src = url;
                document.body.appendChild(iframe);
                setTimeout(() => {
                    if (iframe.parentNode) iframe.parentNode.removeChild(iframe);
                    console.log(LOG_PREFIX, 'iframe removed');
                }, 30_000);
                console.log(LOG_PREFIX, '✓ iframe injected (browser path)');
            }
        } catch (err) {
            console.error(LOG_PREFIX, 'Capture failed:', err);
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

    observer.observe(document.body, { childList: true, subtree: false });

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
