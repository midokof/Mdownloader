/* NovaDownload — browser extension test suite.
 *
 * Runs with the Node.js standard library only (no install, no network):
 *
 *     node tests/extension-tests/run-tests.js
 *     node tests/extension-tests/run-tests.js --package     (also verifies release/ output)
 *
 * It covers what a browser would otherwise have to tell us by hand:
 *   - locale catalogs (parity, every key the code uses, no dead copy);
 *   - placeholder substitution in both the browser path and the fallback path;
 *   - manifests (MV3 shape, permissions, referenced files, ids derived from the public key);
 *   - the detection library (classification, naming, formatting, merging, DOM scanning);
 *   - source hygiene (no innerHTML, no eval, no inline handlers).
 */

'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const crypto = require('crypto');

const repoRoot = path.resolve(__dirname, '..', '..');
const extensionRoot = path.join(repoRoot, 'browser-extensions');
const commonRoot = path.join(extensionRoot, 'common');
const identityPath = path.join(extensionRoot, 'extension-identity.json');
const csharpPath = path.join(repoRoot, 'src', 'NovaDownload.BrowserIntegration', 'BrowserIntegrationInfo.cs');

const EXPECTED_BROWSERS = ['chrome', 'edge', 'firefox'];
const CHROMIUM_PERMISSIONS = ['activeTab', 'scripting', 'storage', 'contextMenus', 'downloads', 'nativeMessaging'];

let passed = 0;
let failed = 0;
const failures = [];

function test(name, fn) {
    try {
        fn();
        passed++;
        process.stdout.write('  ok    ' + name + '\n');
    } catch (error) {
        failed++;
        failures.push(name + ' :: ' + (error && error.message));
        process.stdout.write('  FAIL  ' + name + '\n          ' + (error && error.message) + '\n');
    }
}

function assert(condition, message) {
    if (!condition) throw new Error(message || 'assertion failed');
}

function assertEqual(actual, expected, message) {
    if (actual !== expected) {
        throw new Error((message ? message + ': ' : '') + 'expected ' + JSON.stringify(expected) + ', got ' + JSON.stringify(actual));
    }
}

function assertDeepEqual(actual, expected, message) {
    const a = JSON.stringify(actual);
    const b = JSON.stringify(expected);
    if (a !== b) throw new Error((message ? message + ': ' : '') + 'expected ' + b + ', got ' + a);
}

function readJson(file) {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function section(title) {
    process.stdout.write('\n' + title + '\n');
}

// --------------------------------------------------------------------------- extension sources

const sourceFiles = [];
(function collect(directory) {
    fs.readdirSync(directory, { withFileTypes: true }).forEach(entry => {
        const full = path.join(directory, entry.name);
        if (entry.isDirectory()) {
            if (entry.name === 'icons') return;
            collect(full);
        } else if (/\.(js|html)$/.test(entry.name)) {
            sourceFiles.push(full);
        }
    });
})(commonRoot);

const sourceText = {};
sourceFiles.forEach(file => { sourceText[path.relative(commonRoot, file).replace(/\\/g, '/')] = fs.readFileSync(file, 'utf8'); });

/** Every i18n key the extension actually asks for. */
function usedKeys() {
    const keys = new Set();
    const patterns = [
        /NOVA\.t\(\s*'([A-Za-z0-9_]+)'/g,
        /data-i18n="([A-Za-z0-9_]+)"/g,
        /data-i18n-title="([A-Za-z0-9_]+)"/g,
        /data-i18n-placeholder="([A-Za-z0-9_]+)"/g,
        /setStatus\(\s*'[a-z]+'\s*,\s*'([A-Za-z0-9_]+)'\)/g
    ];
    Object.keys(sourceText).forEach(file => {
        const text = sourceText[file];
        patterns.forEach(pattern => {
            let match;
            while ((match = pattern.exec(text)) !== null) keys.add(match[1]);
        });
    });
    return keys;
}

function fallbackKeys() {
    const text = sourceText['lib/i18n.js'];
    const block = text.slice(text.indexOf('NOVA.FALLBACK = {'), text.indexOf('};', text.indexOf('NOVA.FALLBACK = {')));
    const keys = new Set();
    let match;
    const pattern = /^\s{4}([A-Za-z0-9_]+):/gm;
    while ((match = pattern.exec(block)) !== null) keys.add(match[1]);
    return keys;
}

function catalog(locale) {
    return readJson(path.join(commonRoot, '_locales', locale, 'messages.json'));
}

// --------------------------------------------------------------------------- sandbox

function makeChromeMock(locale, overrides) {
    let catalogData = null;
    try { catalogData = catalog(locale); } catch (error) { catalogData = null; }

    return Object.assign({
        runtime: {
            id: 'pnemgdlefleemkhignbgaclaelffmbah',
            getManifest: () => ({ version: '1.0.0' }),
            get lastError() { return null; },
            sendNativeMessage: () => { }
        },
        i18n: {
            getUILanguage: () => locale,
            getMessage: (key, substitutions) => {
                const entry = catalogData && catalogData[key];
                if (!entry || !entry.message) return '';
                let text = entry.message;
                const subs = substitutions === undefined ? [] : (Array.isArray(substitutions) ? substitutions : [substitutions]);
                Object.keys(entry.placeholders || {}).forEach(name => {
                    const content = String(entry.placeholders[name].content || '');
                    const index = parseInt(content.replace(/[^0-9]/g, ''), 10) - 1;
                    text = text.split('$' + name + '$').join(String(subs[index] === undefined ? '' : subs[index]));
                });
                return text;
            }
        },
        storage: {
            local: {
                get: (defaults, callback) => callback(defaults || {}),
                set: (values, callback) => { if (callback) callback(); }
            },
            session: {
                get: (keys, callback) => callback({}),
                set: (values, callback) => { if (callback) callback(); },
                remove: (key, callback) => { if (callback) callback(); }
            }
        }
    }, overrides || {});
}

function loadLibrary(options) {
    const opts = options || {};
    const sandbox = {
        console: { debug: () => { }, log: () => { }, warn: () => { }, error: () => { } },
        setTimeout: setTimeout,
        clearTimeout: clearTimeout,
        URL: URL,
        navigator: { userAgent: opts.userAgent || 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0' },
        crypto: { randomUUID: (function () { let counter = 0; return function () { counter += 1; return 'uuid-0000-0000-0000-' + String(counter).padStart(12, '0'); }; })() },
        performance: opts.performance || { getEntriesByType: () => [] },
        chrome: makeChromeMock(opts.locale === undefined ? 'en' : opts.locale)
    };
    if (opts.locale === null) sandbox.chrome.i18n.getMessage = () => '';
    vm.createContext(sandbox);
    ['lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'lib/native.js'].forEach(file => {
        vm.runInContext(sourceText[file], sandbox, { filename: file });
    });
    return sandbox;
}

// --------------------------------------------------------------------------- fake DOM

function parseSelector(selector) {
    return selector.split(',').map(part => {
        const match = /^([a-z0-9]+)(?:\[([a-z-]+)\])?$/i.exec(part.trim());
        return match ? { tag: match[1].toUpperCase(), attr: match[2] ? match[2].toLowerCase() : null } : null;
    }).filter(Boolean);
}

function matches(node, selector) {
    return parseSelector(selector).some(rule => node.tagName === rule.tag && (!rule.attr || node.hasAttribute(rule.attr)));
}

function search(nodes, selector) {
    const found = [];
    const walk = list => list.forEach(node => {
        if (matches(node, selector)) found.push(node);
        walk(node.children || []);
    });
    walk(nodes || []);
    return found;
}

function node(tag, attributes, extra, children) {
    const element = {
        tagName: String(tag).toUpperCase(),
        attributes: attributes || {},
        children: children || [],
        getAttribute(name) {
            return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
        },
        hasAttribute(name) {
            return Object.prototype.hasOwnProperty.call(this.attributes, name);
        }
    };
    Object.assign(element, extra || {});
    element.querySelectorAll = selector => search(element.children, selector);
    element.querySelector = selector => {
        const found = search(element.children, selector);
        return found.length ? found[0] : null;
    };
    return element;
}

function fakeDocument(nodes) {
    return {
        title: 'Fixture page',
        querySelectorAll: selector => search(nodes, selector),
        querySelector: selector => {
            const found = search(nodes, selector);
            return found.length ? found[0] : null;
        }
    };
}

// --------------------------------------------------------------------------- crypto helper

function chromiumIdFromKey(base64Key) {
    const alphabet = 'abcdefghijklmnop';
    const hash = crypto.createHash('sha256').update(Buffer.from(base64Key, 'base64')).digest();
    let id = '';
    for (let i = 0; i < 16; i++) {
        id += alphabet[hash[i] >> 4];
        id += alphabet[hash[i] & 0x0f];
    }
    return id;
}

// --------------------------------------------------------------------------- tests

section('i18n catalogs');

test('en and fr catalogs are valid JSON with non-empty messages', () => {
    ['en', 'fr'].forEach(locale => {
        const data = catalog(locale);
        const keys = Object.keys(data);
        assert(keys.length >= 60, locale + ' catalog has only ' + keys.length + ' entries');
        keys.forEach(key => {
            assert(typeof data[key].message === 'string' && data[key].message.length > 0, locale + '.' + key + ' has no message');
            assert(/^[A-Za-z0-9_]+$/.test(key), locale + ' key is not a valid message name: ' + key);
        });
    });
});

test('en and fr catalogs expose exactly the same keys', () => {
    const en = Object.keys(catalog('en')).sort();
    const fr = Object.keys(catalog('fr')).sort();
    assertDeepEqual(fr, en, 'French catalog drifted from English');
});

test('every localized key is safe for both languages (no stray $ signs)', () => {
    ['en', 'fr'].forEach(locale => {
        const data = catalog(locale);
        Object.keys(data).forEach(key => {
            const message = data[key].message;
            const dollarSigns = (message.match(/\$/g) || []).length;
            const placeholders = Object.keys(data[key].placeholders || {}).length;
            assert(dollarSigns === placeholders * 2, locale + '.' + key + ' has ' + dollarSigns + ' dollar signs for ' + placeholders + ' placeholders');
        });
    });
});

test('every key the extension uses exists in both catalogs and the fallback', () => {
    const used = usedKeys();
    const en = catalog('en');
    const fr = catalog('fr');
    const fallback = fallbackKeys();
    const missing = [];
    used.forEach(key => {
        if (!en[key]) missing.push('en:' + key);
        if (!fr[key]) missing.push('fr:' + key);
        if (!fallback.has(key)) missing.push('fallback:' + key);
    });
    assert(missing.length === 0, 'missing messages for ' + missing.join(', '));
});

test('the fallback table has no dead entries', () => {
    const used = usedKeys();
    const dead = [];
    fallbackKeys().forEach(key => { if (!used.has(key)) dead.push(key); });
    assert(dead.length === 0, 'unused fallback copy: ' + dead.join(', '));
});

test('the fallback table has no dead entries beyond the catalog', () => {
    const en = catalog('en');
    const extra = [];
    fallbackKeys().forEach(key => { if (!en[key]) extra.push(key); });
    assert(extra.length === 0, 'fallback keys missing from the English catalog: ' + extra.join(', '));
});

test('substitutions work through the browser catalog', () => {
    const sandbox = loadLibrary({ locale: 'en' });
    assertEqual(sandbox.NOVA.t('sentOk', 'clip.mp4'), 'Added “clip.mp4” to NovaDownload.');
});

test('substitutions work through the French catalog', () => {
    const sandbox = loadLibrary({ locale: 'fr' });
    assertEqual(sandbox.NOVA.t('sentOk', 'clip.mp4'), '« clip.mp4 » ajouté à NovaDownload.');
});

test('substitutions work when no catalog is available', () => {
    const sandbox = loadLibrary({ locale: null });
    assertEqual(sandbox.NOVA.t('sentOk', 'clip.mp4'), 'Added “clip.mp4” to NovaDownload.');
});

test('an unknown key returns the key itself instead of undefined', () => {
    const sandbox = loadLibrary({ locale: 'en' });
    assertEqual(sandbox.NOVA.t('thisKeyDoesNotExist'), 'thisKeyDoesNotExist');
});

test('the shared library detects the browser family from the user agent', () => {
    assertEqual(loadLibrary({ userAgent: 'Mozilla/5.0 ... Edg/140.0.0.0' }).NOVA.browser, 'edge');
    assertEqual(loadLibrary({ userAgent: 'Mozilla/5.0 ... Chrome/140.0.0.0 Safari/537.36' }).NOVA.browser, 'chrome');
    assertEqual(loadLibrary({ userAgent: 'Mozilla/5.0 ... Firefox/141.0' }).NOVA.browser, 'firefox');
});

test('the host name is identical everywhere it appears', () => {
    const csharp = fs.readFileSync(csharpPath, 'utf8');
    const hostName = /HostName = "([^"]+)"/.exec(csharp)[1];
    assertEqual(readJson(identityPath).hostName, hostName, 'identity file');
    assertEqual(loadLibrary({ locale: 'en' }).NOVA.HOST_NAME, hostName, 'library');
});

section('manifests');

const manifests = {};
EXPECTED_BROWSERS.forEach(browser => {
    manifests[browser] = readJson(path.join(extensionRoot, browser, 'manifest.json'));
});

test('chrome and edge manifests are identical', () => {
    assertDeepEqual(manifests.edge, manifests.chrome, 'Edge must match Chrome except for store metadata');
});

test('chromium manifests are MV3 with a module-free service worker', () => {
    ['chrome', 'edge'].forEach(browser => {
        const manifest = manifests[browser];
        assertEqual(manifest.manifest_version, 3, browser);
        assertEqual(manifest.background.service_worker, 'background.js', browser);
        assert(!manifest.background.type, browser + ' must stay a classic worker (importScripts)');
        assertEqual(manifest.default_locale, 'en', browser);
        assertEqual(manifest.name, '__MSG_extName__', browser);
        assertEqual(manifest.description, '__MSG_extDescription__', browser);
    });
});

test('every browser asks for the same permission set and no host permission by default', () => {
    EXPECTED_BROWSERS.forEach(browser => {
        const manifest = manifests[browser];
        assertDeepEqual(manifest.permissions.slice().sort(), CHROMIUM_PERMISSIONS.slice().sort(), browser + ' permissions');
        assertDeepEqual((manifest.optional_permissions || []).slice().sort(), ['webRequest'], browser + ' optional permissions');
        assertDeepEqual((manifest.optional_host_permissions || []).slice().sort(), ['http://*/*', 'https://*/*'], browser + ' optional hosts');
        assert(!manifest.host_permissions, browser + ' must not request host permissions up front');
        assert(!manifest.content_scripts, browser + ' must inject content scripts on demand only');
        assert((manifest.permissions || []).indexOf('<all_urls>') < 0, browser + ' must not use <all_urls>');
    });
});

test('firefox manifest carries the gecko identity and an event page', () => {
    const manifest = manifests.firefox;
    assertEqual(manifest.manifest_version, 3, 'firefox manifest version');
    assertEqual(manifest.browser_specific_settings.gecko.id, 'novadownload@vanta.local');
    assert(parseFloat(manifest.browser_specific_settings.gecko.strict_min_version) >= 128,
        'optional_host_permissions needs Firefox 128+');
    assertDeepEqual(manifest.background.scripts, ['lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'lib/native.js', 'background.js']);
});

test('all browsers ship the same version and icons', () => {
    const version = manifests.chrome.version;
    EXPECTED_BROWSERS.forEach(browser => {
        assertEqual(manifests[browser].version, version, browser + ' version');
        assert(/^\d+\.\d+\.\d+$/.test(version), 'version must be a three-part number');
    });
    const icons = manifests.chrome.icons;
    assertDeepEqual(Object.keys(icons).sort(), ['128', '16', '32', '48']);
});

test('every file a manifest references exists in the shared package', () => {
    const referenced = new Set();
    EXPECTED_BROWSERS.forEach(browser => {
        const manifest = manifests[browser];
        Object.values(manifest.icons).forEach(file => referenced.add(file));
        Object.values(manifest.action.default_icon).forEach(file => referenced.add(file));
        referenced.add(manifest.action.default_popup);
        referenced.add(manifest.options_ui.page);
        if (manifest.background.service_worker) referenced.add(manifest.background.service_worker);
        (manifest.background.scripts || []).forEach(file => referenced.add(file));
    });
    ['lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'lib/native.js'].forEach(file => referenced.add(file));
    referenced.add('content.js');
    referenced.add('popup.css');
    referenced.add('options.css');
    referenced.add('lib/ui.css');
    referenced.add('_locales/en/messages.json');
    referenced.add('_locales/fr/messages.json');

    const missing = [];
    referenced.forEach(file => {
        if (!fs.existsSync(path.join(commonRoot, file))) missing.push(file);
    });
    assert(missing.length === 0, 'missing files: ' + missing.join(', '));
});

test('the chromium extension id is derived from the shipped public key', () => {
    const identity = readJson(identityPath);
    const computed = chromiumIdFromKey(identity.chromiumExtensionKey);
    assertEqual(computed, identity.chromiumExtensionId, 'extension-identity.json out of sync');
    assert(/^[a-p]{32}$/.test(computed), 'chromium ids only use a-p');
    ['chrome', 'edge'].forEach(browser => {
        assertEqual(manifests[browser].key, identity.chromiumExtensionKey, browser + ' manifest key');
        assertEqual(chromiumIdFromKey(manifests[browser].key), identity.chromiumExtensionId, browser + ' derived id');
    });

    const csharp = fs.readFileSync(csharpPath, 'utf8');
    const csharpKey = (/(?:ChromiumExtensionKey\s*=\s*)((?:"[^"]*"\s*\+?\s*)+);/.exec(csharp)[1].match(/"([^"]*)"/g) || [])
        .map(part => part.replace(/"/g, '')).join('');
    assertEqual(csharpKey, identity.chromiumExtensionKey, 'C# public key differs from the manifest key');
    assertEqual(/ChromiumExtensionId = "([a-p]{32})"/.exec(csharp)[1], identity.chromiumExtensionId, 'C# id differs');
    assertEqual(/FirefoxExtensionId = "([^"]+)"/.exec(csharp)[1], identity.firefoxExtensionId, 'C# Firefox id differs');
});

test('the public key is a valid 2048-bit RSA SubjectPublicKeyInfo', () => {
    const key = readJson(identityPath).chromiumExtensionKey;
    const der = Buffer.from(key, 'base64');
    assertEqual(der.length, 294, 'DER length');
    const publicKey = crypto.createPublicKey({ key: der, format: 'der', type: 'spki' });
    assertEqual(publicKey.asymmetricKeyType, 'rsa');
    assertEqual(publicKey.asymmetricKeyDetails.modulusLength, 2048);
});

section('detection library');

test('classify recognises the documented media formats', () => {
    const { NOVA } = loadLibrary({});
    const cases = [
        ['https://cdn.example.com/video.mp4', 'video', 'MP4'],
        ['https://cdn.example.com/video.webm?token=abc', 'video', 'WEBM'],
        ['https://cdn.example.com/audio.mp3', 'audio', 'MP3'],
        ['https://cdn.example.com/song.m4a#t=10', 'audio', 'M4A'],
        ['https://cdn.example.com/sound.ogg', 'audio', 'OGG'],
        ['https://cdn.example.com/stream.m3u8', 'stream', 'M3U8'],
        ['https://cdn.example.com/manifest.mpd', 'stream', 'MPD']
    ];
    cases.forEach(([url, kind, label]) => {
        const info = NOVA.classify(url);
        assert(info, 'not classified: ' + url);
        assertEqual(info.kind, kind, url);
        assertEqual(info.label, label, url);
    });
});

test('classify ignores non-media and non-fetchable URLs', () => {
    const { NOVA } = loadLibrary({});
    ['https://example.com/page.html', 'blob:https://example.com/9d1f', 'data:video/mp4;base64,AAA',
        'chrome://extensions/', 'magnet:?xt=urn:btih:abc', 'about:blank'].forEach(url => {
            assert(!NOVA.classify(url), 'should not classify: ' + url);
        });
});

test('classify falls back on the MIME type when the extension is unhelpful', () => {
    const { NOVA } = loadLibrary({});
    const info = NOVA.classify('https://example.com/play?id=42', 'video/mp4; codecs="avc1"');
    assert(info, 'MIME-only classification failed');
    assertEqual(info.kind, 'video');
    assertEqual(info.mime, 'video/mp4');
});

test('playlists are classified but never presented as downloadable', () => {
    const { NOVA } = loadLibrary({});
    const item = NOVA.toItem('https://cdn.example.com/live/index.m3u8', { source: 'network' });
    assertEqual(item.unsupported, 'stream');
    assert(NOVA.unsupportedReason(item).length > 0, 'a playlist needs an explanation');
});

test('blob playback is reported honestly instead of being offered', () => {
    const { NOVA } = loadLibrary({});
    const item = NOVA.toItem('blob:https://example.com/9d1f', { source: 'media-element' });
    assertEqual(item.unsupported, 'blob');
    assert(NOVA.unsupportedReason(item).indexOf('blob') >= 0);
});

test('toItem refuses anything the engine cannot fetch', () => {
    const { NOVA } = loadLibrary({});
    assertEqual(NOVA.toItem('ftp://example.com/file.mp4', {}), null);
    assertEqual(NOVA.toItem('javascript:alert(1)', {}), null);
    assertEqual(NOVA.toItem('', {}), null);
});

test('file names are derived from the URL and sanitized', () => {
    const { NOVA } = loadLibrary({});
    assertEqual(NOVA.fileNameFromUrl('https://cdn.example.com/a/b/movie%20night.mp4?x=1'), 'movie night.mp4');
    assertEqual(NOVA.fileNameFromUrl('https://cdn.example.com/'), 'cdn.example.com_');
    assert(NOVA.fileNameFromUrl('https://cdn.example.com/' + 'x'.repeat(400) + '.mp4').length <= 180, 'name must be bounded');
});

test('sizes and durations are formatted for humans', () => {
    const { NOVA } = loadLibrary({});
    assertEqual(NOVA.formatBytes(0), '');
    assertEqual(NOVA.formatBytes(900), '900 B');
    assertEqual(NOVA.formatBytes(1024), '1.0 KB');
    assertEqual(NOVA.formatBytes(1024 * 1024 * 3.5), '3.5 MB');
    assertEqual(NOVA.formatDuration(0), '');
    assertEqual(NOVA.formatDuration(95), '1:35');
    assertEqual(NOVA.formatDuration(3725), '1:02:05');
});

test('merging keeps the richest metadata for a repeated URL', () => {
    const { NOVA } = loadLibrary({});
    const merged = NOVA.mergeItems([
        { url: 'https://cdn.example.com/a.mp4', name: 'a.mp4', kind: 'video', label: 'MP4', source: 'link' },
        { url: 'https://cdn.example.com/a.mp4', name: 'a.mp4', kind: 'video', label: 'MP4', source: 'network', size: 4096, duration: 12 }
    ]);
    assertEqual(merged.length, 1);
    assertEqual(merged[0].size, 4096);
    assertEqual(merged[0].duration, 12);
});

test('media elements, links, preload hints, metadata and resource timing are all scanned', () => {
    const { NOVA } = loadLibrary({});
    const video = node('video', { src: '/media/clip.mp4' }, { currentSrc: 'https://page.example/media/clip.mp4', duration: 42.5, videoWidth: 1920, videoHeight: 1080 });
    const empty = node('video', {}, { currentSrc: '', duration: 0 });
    const audio = node('audio', {}, { currentSrc: 'https://page.example/media/song.ogg', duration: 180 });
    const anchor = node('a', { href: '/files/archive.zip', download: 'archive.zip' });
    const plainAnchor = node('a', { href: '/about' });
    const preload = node('link', { rel: 'preload', href: '/media/trailer.webm', as: 'video' });
    const stylesheet = node('link', { rel: 'stylesheet', href: '/app.css' });
    const og = node('meta', { property: 'og:video', content: 'https://cdn.example.com/og.mp4' });

    const doc = fakeDocument([video, empty, audio, anchor, plainAnchor, preload, stylesheet, og]);
    const result = NOVA.scanDocument(doc, 'https://page.example/watch');

    const urls = result.items.map(item => item.url);
    assert(urls.indexOf('https://page.example/media/clip.mp4') >= 0, 'video element missed: ' + urls.join(', '));
    assert(urls.indexOf('https://page.example/media/song.ogg') >= 0, 'audio element missed');
    assert(urls.indexOf('https://page.example/files/archive.zip') >= 0, 'download link missed');
    assert(urls.indexOf('https://page.example/media/trailer.webm') >= 0, 'preload hint missed');
    assert(urls.indexOf('https://cdn.example.com/og.mp4') >= 0, 'og:video metadata missed');
    assert(urls.indexOf('https://page.example/about') < 0, 'a plain link must not be listed');

    const clip = result.items.filter(item => item.url.indexOf('clip.mp4') >= 0)[0];
    assertEqual(clip.width, 1920);
    assertEqual(clip.duration, 42.5);
    assertEqual(clip.source, 'media-element');
});

test('resource timing entries reveal media the markup never exposes', () => {
    const sandbox = loadLibrary({
        performance: {
            getEntriesByType: () => [
                { name: 'https://cdn.example.com/hidden/segment-01.mp4', transferSize: 8192, encodedBodySize: 8000, initiatorType: 'xmlhttprequest' },
                { name: 'https://cdn.example.com/app.js', transferSize: 512, initiatorType: 'script' }
            ]
        }
    });
    const result = sandbox.NOVA.scanDocument(fakeDocument([]), 'https://page.example/player');
    assertEqual(result.items.length, 1);
    assertEqual(result.items[0].source, 'network');
    assertEqual(result.items[0].size, 8192);
});

test('a blob-backed player is reported as unsupported, not as a download', () => {
    const video = node('video', { src: 'blob:https://page.example/9d1f' }, { currentSrc: 'blob:https://page.example/9d1f' });
    const { NOVA } = loadLibrary({});
    const result = NOVA.scanDocument(fakeDocument([video]), 'https://page.example/watch');
    assertEqual(result.items.length, 0, 'blob playback must not be offered');
    assertEqual(result.unsupported.length, 1);
    assertEqual(result.unsupported[0].unsupported, 'blob');
});

test('requests are built with the protocol version, browser family and a unique id', () => {
    const { NOVA } = loadLibrary({});
    const request = NOVA.buildRequest('download', { url: 'https://example.com/a.mp4', empty: '', missing: undefined });
    assertEqual(request.action, 'download');
    assertEqual(request.protocolVersion, NOVA.PROTOCOL_VERSION);
    assertEqual(request.browser, 'edge');
    assert(request.requestId && request.requestId.length > 0, 'a request id is required');
    assert(!('empty' in request), 'empty values must not be sent');
    assert(!('missing' in request), 'undefined values must not be sent');
    assertEqual(NOVA.buildRequest('ping').requestId === request.requestId, false, 'ids must be unique');
});

test('the client refuses anything that is not an http(s) URL before touching the host', () => {
    const { NOVA } = loadLibrary({});
    assert(!NOVA.isFetchable('file:///C:/secret.txt'));
    assert(!NOVA.isFetchable('javascript:alert(1)'));
    assert(NOVA.isFetchable('https://example.com/a.mp4'));
    assert(NOVA.isFetchable('http://127.0.0.1:8080/a.mp4'));
});

section('source hygiene');

test('no extension page builds DOM from untrusted strings with innerHTML', () => {
    const offenders = [];
    Object.keys(sourceText).forEach(file => {
        if (/innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(sourceText[file])) offenders.push(file);
    });
    assert(offenders.length === 0, 'HTML injection risk in ' + offenders.join(', '));
});

test('no eval, no Function constructor, no inline handlers', () => {
    const offenders = [];
    Object.keys(sourceText).forEach(file => {
        const text = sourceText[file];
        if (/\beval\s*\(/.test(text)) offenders.push(file + ' (eval)');
        if (/new\s+Function\s*\(/.test(text)) offenders.push(file + ' (Function)');
        if (/\son[a-z]+\s*=\s*"/.test(text) && file.endsWith('.html')) offenders.push(file + ' (inline handler)');
    });
    assert(offenders.length === 0, 'unexpected code path in ' + offenders.join(', '));
});

test('the background worker never blocks requests and never rewrites them', () => {
    const text = sourceText['background.js'];
    assert(text.indexOf("'blocking'") < 0, 'webRequest must stay observational');
    assert(text.indexOf('onBeforeSendHeaders') < 0 && text.indexOf('onHeadersReceived') < 0, 'no header tampering');
    assert(!/declarativeNetRequest|webRequestBlocking/.test(text), 'no request modification');
});

test('the extension never calls an HTTP endpoint of its own', () => {
    const offenders = [];
    Object.keys(sourceText).forEach(file => {
        if (/fetch\s*\(|XMLHttpRequest|WebSocket|EventSource/.test(sourceText[file])) offenders.push(file);
    });
    assert(offenders.length === 0, 'the extension must only use native messaging: ' + offenders.join(', '));
});

// --------------------------------------------------------------------------- optional packaging check

if (process.argv.indexOf('--package') >= 0) {
    section('release packages');

    EXPECTED_BROWSERS.forEach(browser => {
        const directory = path.join(repoRoot, 'release', browser + '-extension');

        test('release/' + browser + '-extension exists and mirrors the shared code', () => {
            assert(fs.existsSync(directory), 'missing package: ' + directory);
            const manifest = readJson(path.join(directory, 'manifest.json'));
            assertEqual(manifest.version, manifests[browser].version, browser + ' packaged version');
            ['background.js', 'content.js', 'popup.html', 'popup.js', 'options.html', 'options.js',
                'lib/compat.js', 'lib/i18n.js', 'lib/media.js', 'lib/native.js', 'lib/ui.css',
                'icons/icon16.png', 'icons/icon128.png',
                '_locales/en/messages.json', '_locales/fr/messages.json'].forEach(file => {
                    assert(fs.existsSync(path.join(directory, file)), browser + ' package is missing ' + file);
                });
        });

        test('release/' + browser + '-extension keeps the manifest id intact', () => {
            const manifest = readJson(path.join(directory, 'manifest.json'));
            if (browser === 'firefox') {
                assertEqual(manifest.browser_specific_settings.gecko.id, 'novadownload@vanta.local');
            } else {
                assertEqual(chromiumIdFromKey(manifest.key), readJson(identityPath).chromiumExtensionId);
            }
        });
    });
}

// --------------------------------------------------------------------------- summary

process.stdout.write('\n' + passed + ' passed, ' + failed + ' failed\n');
if (failed > 0) {
    process.stdout.write('\nFailures:\n  - ' + failures.join('\n  - ') + '\n');
    process.exit(1);
}
