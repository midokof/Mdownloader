/* NovaDownload — native messaging client.
 *
 * One place that builds the request envelope, talks to the native messaging host and translates
 * host/browser errors into something a user can act on. The host validates everything again, so
 * this file is convenience, not a security boundary.
 */

var NOVA = (typeof NOVA === 'object' && NOVA !== null) ? NOVA : {};

NOVA.DEFAULT_TIMEOUT_MS = 45000;

NOVA.buildRequest = function (action, options) {
    var request = {
        action: action,
        protocolVersion: NOVA.PROTOCOL_VERSION,
        browser: NOVA.browser,
        extensionVersion: NOVA.extensionVersion,
        requestId: NOVA.newId()
    };
    var extra = options || {};
    Object.keys(extra).forEach(function (key) {
        var value = extra[key];
        if (value !== undefined && value !== null && value !== '') request[key] = value;
    });
    return request;
};

NOVA.failure = function (code, message) {
    return { ok: false, code: code, message: message };
};

/** Maps a native messaging failure onto an actionable message. */
NOVA.friendlyHostError = function (rawMessage) {
    var text = String(rawMessage || '');
    if (/not found|no such native/i.test(text)) {
        return NOVA.t('errorHostMissing') + ' ' + NOVA.t('errorHostMissingHint');
    }
    if (/forbidden|not allowed|access to the specified/i.test(text)) {
        return NOVA.t('errorHostMissing') + ' ' + NOVA.t('errorHostMissingHint');
    }
    return text || NOVA.t('errorUnavailable') + ' ' + NOVA.t('errorUnavailableHint');
};

NOVA.sendNative = function (request, timeoutMs) {
    return new Promise(function (resolve) {
        var settled = false;
        var finish = function (value) { if (!settled) { settled = true; resolve(value); } };
        var timer = setTimeout(function () {
            finish(NOVA.failure('APP_UNAVAILABLE', NOVA.t('errorTimeout')));
        }, timeoutMs || NOVA.DEFAULT_TIMEOUT_MS);

        var api = NOVA.api;
        if (!api || !api.runtime || !api.runtime.sendNativeMessage) {
            clearTimeout(timer);
            return finish(NOVA.failure('APP_UNAVAILABLE', NOVA.t('errorUnavailable')));
        }

        try {
            api.runtime.sendNativeMessage(NOVA.HOST_NAME, request, function (response) {
                clearTimeout(timer);
                var error = NOVA.lastError();
                if (error) return finish(NOVA.failure('APP_UNAVAILABLE', NOVA.friendlyHostError(error.message)));
                if (!response) return finish(NOVA.failure('APP_UNAVAILABLE', NOVA.t('errorUnavailable') + ' ' + NOVA.t('errorUnavailableHint')));
                finish(response);
            });
        } catch (e) {
            clearTimeout(timer);
            finish(NOVA.failure('APP_UNAVAILABLE', NOVA.friendlyHostError(e && e.message)));
        }
    });
};

NOVA.pingApp = function () {
    return NOVA.sendNative(NOVA.buildRequest('ping'), 8000);
};

NOVA.openApp = function () {
    return NOVA.sendNative(NOVA.buildRequest('open'), 30000);
};

/** Sends a page URL the user typed or picked. */
NOVA.sendUrl = function (url, options) {
    var opts = options || {};
    if (!NOVA.isFetchable(url)) {
        return Promise.resolve(NOVA.failure('INVALID_REQUEST', NOVA.t('errorInvalidLink')));
    }
    return NOVA.sendNative(NOVA.buildRequest('download', {
        url: url,
        fileName: NOVA.clamp(opts.fileName || '', 180),
        directory: opts.directory || '',
        pageUrl: opts.pageUrl || '',
        mediaType: opts.mediaType || ''
    }));
};

/** Sends a detected media item to the desktop application. */
NOVA.sendItem = function (item, options) {
    var opts = options || {};
    if (!item || !NOVA.isFetchable(item.url)) {
        return Promise.resolve(NOVA.failure('INVALID_REQUEST', NOVA.t('errorInvalidLink')));
    }
    if (item.unsupported) {
        return Promise.resolve(NOVA.failure('UNSUPPORTED_MEDIA', NOVA.unsupportedReason(item)));
    }
    return NOVA.sendNative(NOVA.buildRequest('download', {
        url: item.url,
        fileName: NOVA.clamp(item.name || '', 180),
        directory: opts.directory || '',
        pageUrl: item.pageUrl || '',
        mediaType: item.mime || ''
    }));
};
