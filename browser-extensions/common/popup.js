/* NovaDownload — popup controller.
 *
 * Renders what the page exposes, sends a validated request through the service worker, and keeps
 * the connection state honest: detection still works when the desktop application is closed, so
 * the user can see what is available before starting it.
 */

(function () {
    'use strict';

    var state = {
        tabId: -1,
        tabUrl: '',
        status: 'checking',
        defaultDirectory: '',
        scan: { items: [], unsupported: [], captured: [], hasPlayer: false, blobCount: 0 }
    };

    function $(id) { return document.getElementById(id); }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function send(message, callback) {
        NOVA.api.runtime.sendMessage(message, function (response) {
            var error = NOVA.lastError();
            if (error) {
                if (callback) callback({ ok: false, code: 'APP_UNAVAILABLE', message: error.message });
                return;
            }
            if (callback) callback(response || { ok: false, code: 'APP_UNAVAILABLE', message: NOVA.t('errorGeneric') });
        });
    }

    // ------------------------------------------------------------------ status

    function setStatus(kind, key) {
        state.status = kind;
        var dot = $('statusDot');
        dot.className = 'dot ' + (kind === 'connected' ? 'ok' : kind === 'checking' || kind === 'starting' ? 'busy' : 'off');
        $('statusText').textContent = NOVA.t(key);
    }

    function statusFromResponse(response) {
        if (!response) { setStatus('offline', 'statusOffline'); return; }
        if (response.ok) { setStatus('connected', 'statusConnected'); return; }
        if (response.code === 'DISABLED') { setStatus('offline', 'statusDisabled'); return; }
        setStatus('offline', 'statusOffline');
    }

    // ------------------------------------------------------------------ banner

    function clearBanner() { $('banner').textContent = ''; }

    function showBanner(kind, message, actionLabel, action) {
        var banner = $('banner');
        banner.textContent = '';
        var box = el('div', 'banner ' + kind);
        box.appendChild(el('span', null, message));
        if (actionLabel && action) {
            var button = el('button', 'primary', actionLabel);
            button.addEventListener('click', action);
            box.appendChild(button);
        }
        banner.appendChild(box);
    }

    function reportResponse(response, onDone) {
        statusFromResponse(response);
        if (!response) {
            showBanner('error', NOVA.t('errorGeneric'));
            if (onDone) onDone(false, '');
            return;
        }

        if (response.ok) {
            var message = response.duplicate
                ? NOVA.t('sentDuplicate')
                : (response.fileName ? NOVA.t('sentOk', response.fileName) : NOVA.t('sentOk', ''));
            showBanner('ok', message);
            if (onDone) onDone(true, message);
            return;
        }

        if (response.code === 'DISABLED') {
            showBanner('error', NOVA.t('errorDisabled'));
        } else if (response.code === 'APP_UNAVAILABLE' || /not installed|not running|not registered/i.test(response.message || '')) {
            showBanner('error', response.message || NOVA.t('errorUnavailable'), NOVA.t('btnOpen'), function () {
                openApp();
            });
        } else if (response.code === 'DUPLICATE') {
            showBanner('info', response.message || NOVA.t('sentDuplicate'));
        } else {
            showBanner('error', response.message || NOVA.t('errorGeneric'));
        }
        if (onDone) onDone(false, '');
    }

    function openApp() {
        setStatus('starting', 'statusStarting');
        send({ type: 'nova:open' }, function (response) { reportResponse(response); });
    }

    // ------------------------------------------------------------------ rendering

    function describe(item) {
        var parts = [NOVA.kindLabel(item)];
        if (item.label && item.label !== 'FILE') parts.push(item.label);
        var size = NOVA.formatBytes(item.size);
        if (size) parts.push(size);
        var duration = NOVA.formatDuration(item.duration);
        if (duration) parts.push(duration);
        if (item.width && item.height) parts.push(item.width + '×' + item.height);
        var source = NOVA.sourceLabel(item.source);
        if (source) parts.push(source);
        return parts.join(' · ');
    }

    function buildRow(item, disabled) {
        var row = el('div', 'row');
        row.appendChild(el('span', 'badge ' + (item.kind || 'file'), item.label || 'FILE'));

        var body = el('div', 'body');
        var name = el('span', 'name', item.name || NOVA.fileNameFromUrl(item.url));
        name.title = item.url;
        var meta = el('span', 'meta', describe(item));
        meta.title = item.url;
        body.appendChild(name);
        body.appendChild(meta);

        var actions = el('div', 'actions');
        var button = el('button', 'primary', NOVA.t('btnDownload'));
        button.disabled = !!disabled;
        button.addEventListener('click', function () {
            button.disabled = true;
            button.textContent = NOVA.t('btnSending');
            var slowTimer = setTimeout(function () { setStatus('starting', 'statusStarting'); }, 1500);

            send({ type: 'nova:send-item', item: item }, function (response) {
                clearTimeout(slowTimer);
                reportResponse(response, function (ok, message) {
                    if (ok) {
                        button.textContent = NOVA.t('btnSent');
                        meta.textContent = message;
                    } else {
                        button.textContent = NOVA.t('btnDownload');
                        button.disabled = false;
                    }
                });
            });
        });
        actions.appendChild(button);

        row.appendChild(body);
        row.appendChild(actions);
        return row;
    }

    function renderDetected() {
        var list = $('detected');
        list.textContent = '';
        var items = state.scan.items || [];
        $('detectedCount').textContent = String(items.length);

        items.forEach(function (item) { list.appendChild(buildRow(item, false)); });

        var empty = $('detectedEmpty');
        if (items.length === 0) {
            empty.textContent = '';
            empty.appendChild(el('div', null, NOVA.t('emptyDetected')));
            empty.appendChild(el('div', null,
                state.scan.blobCount > 0 ? NOVA.t('unsupportedBlob') :
                    state.scan.hasPlayer ? NOVA.t('emptyHint') : NOVA.t('emptyHint')));
        } else {
            empty.textContent = '';
        }

        var unsupported = state.scan.unsupported || [];
        $('unsupportedBox').hidden = unsupported.length === 0;
        $('unsupportedCount').textContent = String(unsupported.length);
        var unsupportedList = $('unsupportedList');
        unsupportedList.textContent = '';
        unsupported.forEach(function (item) {
            var row = el('div', 'row');
            row.appendChild(el('span', 'badge ' + (item.kind || 'stream'), item.label || 'N/A'));
            var body = el('div', 'body');
            var name = el('span', 'name', item.name || NOVA.fileNameFromUrl(item.url));
            name.title = item.url;
            body.appendChild(name);
            body.appendChild(el('span', 'meta', NOVA.unsupportedReason(item)));
            row.appendChild(body);
            unsupportedList.appendChild(row);
        });
    }

    function renderCaptured() {
        var items = state.scan.captured || [];
        var section = $('capturedSection');
        section.hidden = items.length === 0;
        $('capturedCount').textContent = String(items.length);
        var list = $('captured');
        list.textContent = '';
        items.forEach(function (item) { list.appendChild(buildRow(item, !!item.unsupported)); });
    }

    function renderHint() {
        var hint = $('destinationHint');
        hint.textContent = state.defaultDirectory
            ? NOVA.t('optDestination') + ': ' + state.defaultDirectory
            : '';
    }

    // ------------------------------------------------------------------ actions

    function refresh() {
        setStatus('checking', 'statusChecking');
        clearBanner();

        send({ type: 'nova:ping' }, function (response) {
            statusFromResponse(response);
            if (response && response.defaultDirectory) {
                state.defaultDirectory = response.defaultDirectory;
                renderHint();
            }
        });

        send({ type: 'nova:scan', tabId: state.tabId }, function (result) {
            if (result && result.ok) {
                state.scan = result;
            } else {
                state.scan = { items: [], unsupported: [], captured: [], hasPlayer: false, blobCount: 0 };
            }
            renderDetected();
            renderCaptured();
        });
    }

    function init() {
        NOVA.applyI18n(document);

        $('refresh').addEventListener('click', refresh);
        $('openApp').addEventListener('click', openApp);
        $('optionsLink').addEventListener('click', function (event) {
            event.preventDefault();
            try { NOVA.api.runtime.openOptionsPage(); } catch (e) { /* ignore */ }
        });
        $('clearCaptured').addEventListener('click', function () {
            send({ type: 'nova:clear-captured', tabId: state.tabId }, function () {
                state.scan.captured = [];
                renderCaptured();
            });
        });
        $('sendPage').addEventListener('click', function () {
            var input = $('pageLink');
            var url = (input.value || '').trim();
            if (!NOVA.isFetchable(url)) {
                showBanner('error', NOVA.t('errorInvalidLink'));
                return;
            }
            var button = $('sendPage');
            button.disabled = true;
            send({ type: 'nova:send-url', url: url, options: { pageUrl: state.tabUrl } }, function (response) {
                button.disabled = false;
                reportResponse(response);
            });
        });

        setStatus('checking', 'statusChecking');
        $('detectedEmpty').textContent = NOVA.t('emptyDetected');

        try {
            NOVA.api.tabs.query({ active: true, currentWindow: true }, function (tabs) {
                var tab = tabs && tabs.length ? tabs[0] : null;
                state.tabId = tab && tab.id !== undefined ? tab.id : -1;
                state.tabUrl = (tab && tab.url) || '';
                $('pageLink').value = state.tabUrl;
                refresh();
            });
        } catch (e) {
            showBanner('error', NOVA.t('errorNoTab'));
        }
    }

    document.addEventListener('DOMContentLoaded', init);
})();
