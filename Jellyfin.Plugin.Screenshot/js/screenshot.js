/**
 * Jellyfin Screenshot Capture — frontend script.
 * Injected into index.html by the plugin on startup.
 * Adds a camera button to the video OSD that captures the current frame via the server API.
 */
(function () {
    'use strict';

    const LOG_PREFIX = '[ScreenshotCapture]';
    const BTN_ID = 'screenshot-capture-btn';

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

        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            e.preventDefault();
            captureScreenshot();
        });

        settingsBtn.parentElement.insertBefore(btn, settingsBtn);
        console.log(LOG_PREFIX, 'Button added to OSD.');
    }

    /**
     * Removes the screenshot button when leaving the video page.
     */
    function removeButton() {
        document.getElementById(BTN_ID)?.remove();
    }

    /**
     * Returns the current playback position in Jellyfin ticks (1 tick = 100 ns).
     *
     * In the browser the HTML5 <video> element carries the real position.
     * In Jellyfin Media Player (JMP) the video is decoded by libmpv — the <video>
     * element exists but its currentTime stays at 0. We fall back to the Sessions
     * API which always reflects the authoritative server-side position.
     */
    async function getPositionTicks() {
        const video = document.querySelector('video');
        console.debug(LOG_PREFIX, 'video element:', video, 'currentTime:', video?.currentTime);

        if (video && video.currentTime > 0) {
            const ticks = Math.round(video.currentTime * 10_000_000);
            console.log(LOG_PREFIX, `Position from video element: ${video.currentTime}s → ${ticks} ticks`);
            return ticks;
        }

        console.log(LOG_PREFIX, 'video.currentTime unavailable, falling back to Sessions API');

        // Native / desktop client fallback: ask the Sessions API
        try {
            const serverAddress = ApiClient.serverAddress().replace(/\/$/, '');
            const apiKey = ApiClient.accessToken();
            const sessionsUrl = `${serverAddress}/Sessions?api_key=${encodeURIComponent(apiKey)}`;
            console.debug(LOG_PREFIX, 'Fetching sessions:', sessionsUrl);

            const res = await fetch(sessionsUrl);
            console.debug(LOG_PREFIX, 'Sessions response status:', res.status);
            if (!res.ok) {
                console.warn(LOG_PREFIX, 'Sessions API returned', res.status);
                return 0;
            }

            const sessions = await res.json();
            const deviceId = ApiClient.deviceId();
            console.debug(LOG_PREFIX, `Looking for deviceId=${deviceId} in ${sessions.length} sessions`);

            const mine = sessions.find(s => s.DeviceId === deviceId);
            const ticks = mine?.PlayState?.PositionTicks ?? 0;
            console.log(LOG_PREFIX, `Position from Sessions API: ${ticks} ticks (session found: ${!!mine})`);
            return ticks;
        } catch (err) {
            console.warn(LOG_PREFIX, 'Sessions API fallback failed:', err);
            return 0;
        }
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
    async function captureScreenshot() {
        console.log(LOG_PREFIX, '--- captureScreenshot start ---');

        const itemId = getCurrentItemId();
        console.log(LOG_PREFIX, 'itemId:', itemId);
        if (!itemId) {
            console.warn(LOG_PREFIX, 'Could not determine item ID from OSD.');
            return;
        }

        const positionTicks = await getPositionTicks();
        console.log(LOG_PREFIX, 'positionTicks resolved:', positionTicks);

        const serverAddress = ApiClient.serverAddress().replace(/\/$/, '');
        const apiKey = ApiClient.accessToken();

        const url = `${serverAddress}/Screenshot/capture`
            + `?itemId=${encodeURIComponent(itemId)}`
            + `&positionTicks=${positionTicks}`
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
