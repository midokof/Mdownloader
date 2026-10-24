/* NovaDownload — shared runtime helpers.
 *
 * Loaded in every context: service worker (importScripts), popup, options page and content
 * script. Deliberately uses the `chrome.*` callback namespace, which Chrome, Edge and Firefox
 * all implement (Firefox keeps callbacks and `chrome.runtime.lastError` for compatibility),
 * so one code path serves all three browsers.
 */

var NOVA = (typeof NOVA === 'object' && NOVA !== null) ? NOVA : {};

NOVA.HOST_NAME = 'com.novadownload.host';
NOVA.PROTOCOL_VERSION = 1;
NOVA.HOST_EXE = 'NovaDownload.NativeHost.exe';
NOVA.APP_EXE = 'NovaDownload.App.exe';

NOVA.api = (typeof chrome !== 'undefined' && chrome && chrome.runtime)
    ? chrome
    : (typeof browser !== 'undefined' && browser && browser.runtime ? browser : null);

NOVA.browser = (function () {
    var ua = (typeof navigator !== 'undefined' && navigator.userAgent) ? navigator.userAgent : '';
    if (/Edg\//.test(ua)) return 'edge';
    if (/Firefox\//.test(ua)) return 'firefox';
    if (/OPR\//.test(ua)) return 'opera';
    if (/Chrome\//.test(ua)) return 'chrome';
    return 'unknown';
})();

NOVA.isFirefox = NOVA.browser === 'firefox';

/** The only origins the optional deep-scan permission ever asks for: media is http/https. */
NOVA.DEEP_SCAN_ORIGINS = ['http://*/*', 'https://*/*'];

NOVA.extensionVersion = (function () {
    try { return NOVA.api.runtime.getManifest().version; } catch (e) { return '0.0.0'; }
})();

NOVA.extensionId = (function () {
    try { return NOVA.api.runtime.id; } catch (e) { return ''; }
})();

/** Last runtime error, or null. Call it synchronously inside a callback. */
NOVA.lastError = function () {
    try { return NOVA.api && NOVA.api.runtime ? NOVA.api.runtime.lastError : null; } catch (e) { return null; }
};

NOVA.newId = function () {
    try {
        if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
    } catch (e) { /* fall through */ }
    return 'req-' + Date.now().toString(16) + '-' + Math.random().toString(16).slice(2, 10);
};

NOVA.clamp = function (value, max) {
    var text = String(value === undefined || value === null ? '' : value);
    return text.length <= max ? text : text.slice(0, max);
};

/** True for the only two schemes the desktop engine can fetch. */
NOVA.isFetchable = function (url) {
    return typeof url === 'string' && /^https?:\/\//i.test(url);
};

/** Settings cache shared by every context. Defaults are the conservative ones. */
NOVA.DEFAULT_SETTINGS = {
    autoCapture: false,
    deepScan: false,
    contextMenus: true,
    defaultDirectory: ''
};

NOVA.getSettings = function () {
    return new Promise(function (resolve) {
        try {
            NOVA.api.storage.local.get({ novadownload: NOVA.DEFAULT_SETTINGS }, function (result) {
                resolve(Object.assign({}, NOVA.DEFAULT_SETTINGS, result && result.novadownload ? result.novadownload : {}));
            });
        } catch (e) {
            resolve(Object.assign({}, NOVA.DEFAULT_SETTINGS));
        }
    });
};

NOVA.saveSettings = function (settings) {
    return new Promise(function (resolve) {
        try {
            NOVA.api.storage.local.set({ novadownload: settings }, function () { resolve(settings); });
        } catch (e) {
            resolve(settings);
        }
    });
};
