/* NovaDownload — service worker / event page.
 *
 * Responsibilities:
 *   - relay popup and context-menu actions to the native messaging host;
 *   - scan the active tab on demand through the content script;
 *   - remember media the network or the browser download list revealed for each tab;
 *   - keep the toolbar badge honest about how many items were seen.
 *
 * Nothing is ever downloaded, redirected or modified by the browser itself.
 */

/* The service worker needs its helpers explicitly; a Firefox event page already loads them
 * through the manifest. The guard keeps both shapes working with the same file. */
if (typeof importScripts === 'function') {
    importScripts('lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'lib/native.js');
}

var novaState = {
    settings: Object.assign({}, NOVA.DEFAULT_SETTINGS),
    deepScanActive: false,
    captured: {}          // tabId -> [item]
};

var SESSION = (NOVA.api.storage && NOVA.api.storage.session) ? NOVA.api.storage.session : NOVA.api.storage.local;

const MAX_CAPTURED_PER_TAB = 60;

function log() {
    try { console.debug.apply(console, ['[novadownload]'].concat(Array.prototype.slice.call(arguments))); } catch (e) { /* ignore */ }
}

// ---------------------------------------------------------------- captured media

function remember(tabId, item) {
    if (tabId === undefined || tabId === null || tabId < 0 || !item || !item.url) return;
    var list = novaState.captured[tabId] || [];
    if (list.some(function (existing) { return existing.url === item.url; })) return;
    list.unshift(item);
    if (list.length > MAX_CAPTURED_PER_TAB) list = list.slice(0, MAX_CAPTURED_PER_TAB);
    novaState.captured[tabId] = list;

    var payload = {};
    payload['tab:' + tabId] = list;
    try { SESSION.set(payload); } catch (e) { /* best effort */ }

    updateBadge(tabId);
}

function forget(tabId) {
    delete novaState.captured[tabId];
    try { SESSION.remove('tab:' + tabId); } catch (e) { /* ignore */ }
    updateBadge(tabId);
}

function restoreCaptured() {
    try {
        SESSION.get(null, function (everything) {
            Object.keys(everything || {}).forEach(function (key) {
                if (key.indexOf('tab:') !== 0) return;
                var tabId = Number(key.slice(4));
                if (isFinite(tabId)) novaState.captured[tabId] = everything[key] || [];
            });
        });
    } catch (e) { /* ignore */ }
}

function capturedFor(tabId) {
    return (novaState.captured[tabId] || []).slice();
}

function updateBadge(tabId) {
    var count = (novaState.captured[tabId] || []).length;
    try {
        NOVA.api.action.setBadgeText({ tabId: tabId, text: count > 0 ? String(count) : '' });
        NOVA.api.action.setBadgeBackgroundColor({ color: '#3B82F6' });
    } catch (e) { /* ignore */ }
}

function flashMessage(tabId, ok, message) {
    try {
        NOVA.api.action.setBadgeText({ tabId: tabId, text: ok ? '✓' : '!' });
        NOVA.api.action.setBadgeBackgroundColor({ color: ok ? '#22C55E' : '#EF4444' });
        NOVA.api.action.setTitle({ tabId: tabId, title: 'NovaDownload — ' + (message || '') });
    } catch (e) { /* ignore */ }
    setTimeout(function () { updateBadge(tabId); }, 6000);
}

// ---------------------------------------------------------------- settings + permissions

function loadSettings() {
    return NOVA.getSettings().then(function (settings) {
        novaState.settings = settings;
        configureContextMenus();
        return refreshDeepScan();
    });
}

function hasDeepScanPermission() {
    return new Promise(function (resolve) {
        try {
            NOVA.api.permissions.contains({ permissions: ['webRequest'], origins: NOVA.DEEP_SCAN_ORIGINS }, function (granted) {
                resolve(!!granted);
            });
        } catch (e) {
            resolve(false);
        }
    });
}

function deepScanListener(details) {
    if (details.tabId < 0) return;
    var info = NOVA.classify(details.url);
    if (!info) return;
    remember(details.tabId, {
        url: details.url,
        name: NOVA.fileNameFromUrl(details.url),
        kind: info.kind,
        label: info.label,
        mime: info.mime,
        source: 'network',
        size: 0,
        duration: 0,
        unsupported: info.playlist ? 'stream' : null,
        pageUrl: details.initiator || ''
    });
}

function refreshDeepScan() {
    return hasDeepScanPermission().then(function (granted) {
        if (granted && !novaState.deepScanActive) {
            try {
                NOVA.api.webRequest.onBeforeRequest.addListener(deepScanListener, { urls: NOVA.DEEP_SCAN_ORIGINS });
                novaState.deepScanActive = true;
                log('deep network detection attached');
            } catch (e) {
                novaState.deepScanActive = false;
                log('deep network detection unavailable', e && e.message);
            }
        }
        if (!granted && novaState.deepScanActive) {
            try { NOVA.api.webRequest.onBeforeRequest.removeListener(deepScanListener); } catch (e) { /* ignore */ }
            novaState.deepScanActive = false;
        }
        return novaState.deepScanActive;
    });
}

function configureContextMenus() {
    try {
        NOVA.api.contextMenus.removeAll(function () {
            if (!novaState.settings.contextMenus) return;
            NOVA.api.contextMenus.create({
                id: 'nova:send-link',
                title: NOVA.t('ctxSendLink'),
                contexts: ['link', 'video', 'audio']
            });
            NOVA.api.contextMenus.create({
                id: 'nova:send-page',
                title: NOVA.t('ctxSendPage'),
                contexts: ['page', 'selection']
            });
        });
    } catch (e) { /* ignore */ }
}

// ---------------------------------------------------------------- scanning

function injectScanScripts(tabId) {
    return new Promise(function (resolve) {
        var files = ['lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'content.js'];
        try {
            NOVA.api.scripting.executeScript({ target: { tabId: tabId }, files: files }, function () {
                var error = NOVA.lastError();
                if (error) {
                    log('injection refused', error.message);
                    return resolve(false);
                }
                resolve(true);
            });
        } catch (e) {
            resolve(false);
        }
    });
}

function askContentScript(tabId) {
    return new Promise(function (resolve) {
        try {
            NOVA.api.tabs.sendMessage(tabId, { type: 'nova:scan-here' }, function (response) {
                var error = NOVA.lastError();
                if (error || !response) return resolve({ items: [], unsupported: [], hasPlayer: false, blobCount: 0 });
                resolve(response);
            });
        } catch (e) {
            resolve({ items: [], unsupported: [], hasPlayer: false, blobCount: 0 });
        }
    });
}

function scanTab(tabId) {
    if (tabId === undefined || tabId === null || tabId < 0) {
        return Promise.resolve({ ok: false, items: [], unsupported: [], captured: [] });
    }
    return injectScanScripts(tabId).then(function () {
        return askContentScript(tabId);
    }).then(function (result) {
        var page = NOVA.mergeItems((result.items || []).concat(capturedFor(tabId)));
        return {
            ok: true,
            items: page.filter(function (item) { return !item.unsupported; }),
            unsupported: NOVA.mergeItems((result.unsupported || []).concat(page.filter(function (item) { return !!item.unsupported; }))),
            captured: capturedFor(tabId),
            hasPlayer: !!result.hasPlayer,
            blobCount: result.blobCount || 0,
            url: result.url || '',
            title: result.title || ''
        };
    });
}

// ---------------------------------------------------------------- message router

NOVA.api.runtime.onMessage.addListener(function (message, sender, sendResponse) {
    if (!message || !message.type) return false;

    switch (message.type) {
        case 'nova:ping':
            NOVA.pingApp().then(sendResponse);
            return true;

        case 'nova:scan':
            scanTab(message.tabId).then(sendResponse);
            return true;

        case 'nova:send-item':
            NOVA.sendItem(message.item, message.options).then(sendResponse);
            return true;

        case 'nova:send-url':
            NOVA.sendUrl(message.url, message.options).then(sendResponse);
            return true;

        case 'nova:open':
            NOVA.openApp().then(sendResponse);
            return true;

        case 'nova:clear-captured':
            forget(message.tabId);
            sendResponse({ ok: true });
            return false;

        case 'nova:permissions-changed':
            refreshDeepScan().then(function (active) { sendResponse({ deepScanActive: active }); });
            return true;

        case 'nova:settings-changed':
            NOVA.getSettings().then(function (settings) {
                novaState.settings = settings;
                configureContextMenus();
                sendResponse({ ok: true, settings: settings });
            });
            return true;

        default:
            return false;
    }
});

// ---------------------------------------------------------------- browser integration

function attachDownloadObserver() {
    try {
        NOVA.api.downloads.onCreated.addListener(function (download) {
            if (!novaState.settings.autoCapture || !download) return;
            if (download.byExtensionId && download.byExtensionId === NOVA.extensionId) return;
            if (!NOVA.isFetchable(download.url)) return;

            var info = NOVA.classify(download.url, download.mime);
            var name = download.filename ? download.filename.split(/[\\/]/).pop() : '';
            var item = NOVA.toItem(download.url, {
                name: name || NOVA.fileNameFromUrl(download.url),
                kind: info ? info.kind : 'file',
                mime: download.mime || '',
                size: download.fileSize || download.totalBytes || 0,
                source: 'browser-download'
            });
            if (item && download.tabId !== undefined) remember(download.tabId, item);
        });
    } catch (e) { /* downloads permission missing: the feature is simply absent */ }
}

function attachContextMenus() {
    try {
        NOVA.api.contextMenus.onClicked.addListener(function (info, tab) {
            var url = info.linkUrl || info.srcUrl || info.pageUrl;
            var tabId = tab && tab.id !== undefined ? tab.id : -1;
            if (!NOVA.isFetchable(url)) {
                flashMessage(tabId, false, NOVA.t('errorInvalidLink'));
                return;
            }
            NOVA.sendUrl(url, { pageUrl: (tab && tab.url) || '' }).then(function (response) {
                flashMessage(tabId, !!(response && response.ok), (response && response.message) || '');
                if (tabId >= 0 && response && response.ok) {
                    var item = NOVA.toItem(url, { source: 'link', pageUrl: (tab && tab.url) || '' });
                    if (item) remember(tabId, item);
                }
            });
        });
    } catch (e) { /* ignore */ }
}

function attachTabLifecycle() {
    try {
        NOVA.api.tabs.onRemoved.addListener(function (tabId) { forget(tabId); });
        NOVA.api.tabs.onUpdated.addListener(function (tabId, changeInfo) {
            if (changeInfo && changeInfo.status === 'loading') forget(tabId);
        });
    } catch (e) { /* ignore */ }
}

// ---------------------------------------------------------------- bootstrap

restoreCaptured();
loadSettings();
attachDownloadObserver();
attachContextMenus();
attachTabLifecycle();

NOVA.api.runtime.onInstalled.addListener(function () {
    loadSettings();
});

NOVA.api.runtime.onStartup.addListener(function () {
    loadSettings();
});

NOVA.api.permissions.onAdded.addListener(function () { refreshDeepScan(); });
NOVA.api.permissions.onRemoved.addListener(function () { refreshDeepScan(); });
