/* NovaDownload — options page controller. */

(function () {
    'use strict';

    function $(id) { return document.getElementById(id); }

    function banner(kind, message) {
        var host = $('banner');
        host.textContent = '';
        var box = document.createElement('div');
        box.className = 'banner ' + kind;
        box.textContent = message;
        host.appendChild(box);
    }

    function setStatus(kind, key) {
        $('statusDot').className = 'dot ' + (kind === 'connected' ? 'ok' : kind === 'checking' ? 'busy' : 'off');
        $('statusText').textContent = NOVA.t(key);
    }

    function testConnection() {
        setStatus('checking', 'statusChecking');
        banner('info', NOVA.t('statusChecking'));
        NOVA.api.runtime.sendMessage({ type: 'nova:ping' }, function (response) {
            var error = NOVA.lastError();
            if (error || !response || !response.ok) {
                setStatus('offline', 'statusOffline');
                var message = (response && response.message) || (error && error.message) || NOVA.t('errorUnavailable');
                banner('error', message + ' ' + NOVA.t('errorUnavailableHint'));
                return;
            }
            setStatus('connected', 'statusConnected');
            var detail = response.appVersion ? ' — v' + response.appVersion : '';
            banner('ok', NOVA.t('statusConnected') + detail);
            if (response.defaultDirectory) $('destination').textContent = response.defaultDirectory;
        });
    }

    function loadSettings() {
        NOVA.getSettings().then(function (settings) {
            $('autoCapture').checked = !!settings.autoCapture;
            $('contextMenus').checked = !!settings.contextMenus;
            $('deepScan').checked = !!settings.deepScan;
        });
    }

    function saveSettings(patch) {
        return NOVA.getSettings().then(function (settings) {
            var merged = Object.assign({}, settings, patch);
            return NOVA.saveSettings(merged).then(function () {
                NOVA.api.runtime.sendMessage({ type: 'nova:settings-changed' }, function () { /* refresh badge state */ });
                return merged;
            });
        });
    }

    function requestDeepScanPermission() {
        return new Promise(function (resolve) {
            try {
                NOVA.api.permissions.request({ permissions: ['webRequest'], origins: NOVA.DEEP_SCAN_ORIGINS }, function (granted) {
                    resolve(!!granted);
                });
            } catch (e) {
                resolve(false);
            }
        });
    }

    function init() {
        NOVA.applyI18n(document);

        $('hostName').textContent = NOVA.HOST_NAME;
        $('extensionId').textContent = NOVA.extensionId;
        $('version').textContent = NOVA.extensionVersion;
        $('protocol').textContent = 'v' + NOVA.PROTOCOL_VERSION;
        $('protocol2').textContent = 'v' + NOVA.PROTOCOL_VERSION;
        $('destination').textContent = '—';
        $('registerCommand').textContent = '"' + NOVA.HOST_EXE + '" --register';

        $('testConnection').addEventListener('click', testConnection);

        $('copyCommand').addEventListener('click', function () {
            var text = $('registerCommand').textContent;
            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(text);
                    banner('ok', NOVA.t('optCopied'));
                    return;
                }
            } catch (e) { /* fall through */ }
            banner('info', text);
        });

        $('autoCapture').addEventListener('change', function () {
            saveSettings({ autoCapture: $('autoCapture').checked });
        });

        $('contextMenus').addEventListener('change', function () {
            saveSettings({ contextMenus: $('contextMenus').checked });
        });

        $('deepScan').addEventListener('change', function () {
            var wanted = $('deepScan').checked;
            if (!wanted) {
                try { NOVA.api.permissions.remove({ permissions: ['webRequest'], origins: NOVA.DEEP_SCAN_ORIGINS }); } catch (e) { /* ignore */ }
                saveSettings({ deepScan: false });
                return;
            }
            requestDeepScanPermission().then(function (granted) {
                if (!granted) {
                    $('deepScan').checked = false;
                    banner('error', NOVA.t('optPermissionDenied'));
                    return;
                }
                saveSettings({ deepScan: true });
            });
        });

        loadSettings();
        testConnection();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
