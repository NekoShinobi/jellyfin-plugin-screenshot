/**
 * Screenshot Capture — translations.
 * The server prepends window.ScreenshotCaptureLocales, built from js/locales/*.json.
 * Strings follow Jellyfin's display language (the <html lang> set by jellyfin-web),
 * then the browser languages, and fall back to English per key.
 */
(function () {
    'use strict';

    if (window.ScreenshotCaptureI18n) return;

    const locales = {};
    for (const [code, strings] of Object.entries(window.ScreenshotCaptureLocales || {})) {
        locales[code.toLowerCase()] = strings;
    }
    const english = locales.en || {};
    // A bare language falls back to its most widely used published regional variant.
    const aliases = { pt: 'pt-br', zh: 'zh-cn', 'zh-hans': 'zh-cn', 'zh-sg': 'zh-cn' };
    // Traditional Chinese readers should get English rather than Simplified characters.
    const noBaseFallback = /^zh-(tw|hk|mo|hant)/;

    function resolve(tag) {
        const code = String(tag || '').trim().replace(/_/g, '-').toLowerCase();
        if (!code) return null;
        if (locales[code]) return code;
        if (aliases[code] && locales[aliases[code]]) return aliases[code];
        if (noBaseFallback.test(code)) return null;
        const base = code.split('-')[0];
        if (locales[base]) return base;
        if (aliases[base] && locales[aliases[base]]) return aliases[base];
        return null;
    }

    function locale() {
        // Read on every call so a changed display language applies without a reload.
        const candidates = [document.documentElement.lang, ...(navigator.languages || [navigator.language])];
        for (const candidate of candidates) {
            const code = resolve(candidate);
            if (code) return code;
        }
        return 'en';
    }

    function format(template, params) {
        return template.replace(/\{(\w+)\}/g, (match, name) =>
            params && Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : match);
    }

    /** Returns the translated string for key, with {name} placeholders replaced from params. */
    function t(key, params) {
        const template = locales[locale()]?.[key] ?? english[key] ?? key;
        return format(template, params);
    }

    const escapeHtml = text => String(text).replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    /**
     * Returns the translation escaped for innerHTML. Values in markup are inserted
     * as-is, so translations can position elements such as <strong> in the sentence.
     */
    function html(key, markup) {
        return format(escapeHtml(t(key)), markup);
    }

    /** Formats a number for the current language, e.g. 12.5 or 12,5. */
    function number(value, maximumFractionDigits = 1) {
        try {
            return new Intl.NumberFormat(locale(), { maximumFractionDigits }).format(value);
        } catch (_) {
            return String(Number(value.toFixed(maximumFractionDigits)));
        }
    }

    window.ScreenshotCaptureI18n = { t, html, number, locale, escapeHtml };
})();
