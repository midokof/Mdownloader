/* NovaDownload — media classification and page scanning.
 *
 * Pure functions plus one DOM walker, shared by the popup (via the content script), the service
 * worker and the test harness. Everything here is read-only: nothing is intercepted, nothing is
 * fetched, nothing is executed.
 */

var NOVA = (typeof NOVA === 'object' && NOVA !== null) ? NOVA : {};

/** Extension -> media description. Playlists are listed but explicitly marked unsupported. */
NOVA.MEDIA_TYPES = {
    mp4: { kind: 'video', mime: 'video/mp4' },
    m4v: { kind: 'video', mime: 'video/x-m4v' },
    mov: { kind: 'video', mime: 'video/quicktime' },
    webm: { kind: 'video', mime: 'video/webm' },
    mkv: { kind: 'video', mime: 'video/x-matroska' },
    ogv: { kind: 'video', mime: 'video/ogg' },
    avi: { kind: 'video', mime: 'video/x-msvideo' },
    flv: { kind: 'video', mime: 'video/x-flv' },
    '3gp': { kind: 'video', mime: 'video/3gpp' },
    mp3: { kind: 'audio', mime: 'audio/mpeg' },
    m4a: { kind: 'audio', mime: 'audio/mp4' },
    aac: { kind: 'audio', mime: 'audio/aac' },
    ogg: { kind: 'audio', mime: 'audio/ogg' },
    oga: { kind: 'audio', mime: 'audio/ogg' },
    opus: { kind: 'audio', mime: 'audio/opus' },
    wav: { kind: 'audio', mime: 'audio/wav' },
    flac: { kind: 'audio', mime: 'audio/flac' },
    weba: { kind: 'audio', mime: 'audio/webm' },
    m3u8: { kind: 'stream', mime: 'application/vnd.apple.mpegurl', playlist: 'hls' },
    mpd: { kind: 'stream', mime: 'application/dash+xml', playlist: 'dash' }
};

NOVA.MIME_KINDS = {
    'video/mp4': 'video',
    'video/webm': 'video',
    'video/ogg': 'video',
    'video/quicktime': 'video',
    'audio/mpeg': 'audio',
    'audio/mp4': 'audio',
    'audio/ogg': 'audio',
    'audio/webm': 'audio',
    'audio/wav': 'audio',
    'audio/flac': 'audio'
};

NOVA.extensionOf = function (url) {
    try {
        var path = new URL(url, 'https://nova.invalid').pathname;
        var dot = path.lastIndexOf('.');
        if (dot < 0) return '';
        return path.slice(dot + 1).toLowerCase().replace(/[^a-z0-9]/g, '');
    } catch (e) {
        return '';
    }
};

/** Classifies a URL. Returns null when it is not a recognised media resource. */
NOVA.classify = function (url, mimeType) {
    if (typeof url !== 'string' || !url) return null;
    if (!NOVA.isFetchable(url)) return null;
    if (/^(blob|data|filesystem):/i.test(url)) return null;

    var extension = NOVA.extensionOf(url);
    var known = extension && NOVA.MEDIA_TYPES[extension];
    if (known) {
        return {
            extension: extension,
            kind: known.kind,
            mime: known.mime,
            playlist: known.playlist || null,
            label: extension.toUpperCase()
        };
    }

    if (mimeType) {
        var normalized = String(mimeType).split(';')[0].trim().toLowerCase();
        var kind = NOVA.MIME_KINDS[normalized];
        if (kind) {
            return {
                extension: extension || normalized.split('/')[1],
                kind: kind,
                mime: normalized,
                playlist: null,
                label: (extension || normalized.split('/')[1]).toUpperCase()
            };
        }
    }

    return null;
};

NOVA.fileNameFromUrl = function (url) {
    try {
        var parsed = new URL(url, 'https://nova.invalid');
        var segments = parsed.pathname.split('/').filter(Boolean);
        var last = segments.length ? segments[segments.length - 1] : '';
        var decoded = '';
        try { decoded = decodeURIComponent(last); } catch (e) { decoded = last; }
        decoded = decoded.split('?')[0].trim();
        if (decoded && decoded !== '/') return NOVA.clamp(decoded, 180);
        var fallback = (parsed.hostname + parsed.pathname).replace(/[^A-Za-z0-9._-]+/g, '_');
        return NOVA.clamp(fallback || 'download', 180);
    } catch (e) {
        return 'download';
    }
};

NOVA.formatBytes = function (bytes) {
    var value = Number(bytes);
    if (!isFinite(value) || value <= 0) return '';
    var units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var index = 0;
    while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
    return (index === 0 ? value.toFixed(0) : value.toFixed(value < 10 ? 1 : 0)) + ' ' + units[index];
};

NOVA.formatDuration = function (seconds) {
    var total = Number(seconds);
    if (!isFinite(total) || total <= 0) return '';
    total = Math.round(total);
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var secs = total % 60;
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return hours > 0 ? hours + ':' + pad(minutes) + ':' + pad(secs) : minutes + ':' + pad(secs);
};

NOVA.kindLabel = function (item) {
    if (item.kind === 'stream') return NOVA.t('kindStream');
    if (item.kind === 'audio') return NOVA.t('kindAudio');
    if (item.kind === 'video') return NOVA.t('kindVideo');
    return NOVA.t('kindFile');
};

NOVA.sourceLabel = function (source) {
    switch (source) {
        case 'media-element': return NOVA.t('sourceMediaElement');
        case 'link': return NOVA.t('sourceLink');
        case 'network': return NOVA.t('sourceNetwork');
        case 'preload': return NOVA.t('sourcePreload');
        case 'meta': return NOVA.t('sourceMeta');
        case 'browser-download': return NOVA.t('sourceBrowserDownload');
        default: return source || '';
    }
};

/** Merges duplicates by URL, preferring the entry that carries the richest metadata. */
NOVA.mergeItems = function (items) {
    var byUrl = {};
    var order = [];
    (items || []).forEach(function (item) {
        if (!item || !item.url) return;
        var key = item.url;
        if (!byUrl[key]) {
            byUrl[key] = item;
            order.push(key);
            return;
        }
        var existing = byUrl[key];
        byUrl[key] = {
            url: existing.url,
            name: existing.name || item.name,
            kind: existing.kind === 'file' ? item.kind : existing.kind,
            label: existing.label || item.label,
            mime: existing.mime || item.mime,
            size: existing.size || item.size,
            duration: existing.duration || item.duration,
            width: existing.width || item.width,
            height: existing.height || item.height,
            source: existing.source || item.source,
            pageUrl: existing.pageUrl || item.pageUrl,
            unsupported: item.unsupported || existing.unsupported || null
        };
    });
    return order.map(function (key) { return byUrl[key]; });
};

/** Normalizes a raw URL into a media item, or null when it cannot be downloaded. */
NOVA.toItem = function (url, options) {
    var opts = options || {};
    var value = String(url || '').trim();
    if (!value) return null;

    if (/^(blob|data|filesystem):/i.test(value)) {
        return {
            url: value,
            name: opts.name || 'media',
            kind: 'stream',
            label: 'BLOB',
            source: opts.source || 'media-element',
            unsupported: 'blob',
            pageUrl: opts.pageUrl || '',
            size: 0,
            duration: opts.duration || 0
        };
    }

    if (!NOVA.isFetchable(value)) return null;

    var info = NOVA.classify(value, opts.mime) || {};
    var item = {
        url: value,
        name: NOVA.clamp(opts.name || NOVA.fileNameFromUrl(value), 180),
        kind: opts.kind || info.kind || 'file',
        label: info.label || (opts.kind || 'FILE').toUpperCase(),
        mime: info.mime || opts.mime || '',
        size: opts.size || 0,
        duration: opts.duration || 0,
        width: opts.width || 0,
        height: opts.height || 0,
        source: opts.source || 'link',
        pageUrl: opts.pageUrl || '',
        unsupported: null
    };

    if (info.playlist) item.unsupported = 'stream';
    if (/^magnet:/i.test(value) || /^(mms|rtsp|rtmp:)/i.test(value)) item.unsupported = 'scheme';
    return item;
};

/**
 * Reads every media hint a page exposes: media elements, links, preload hints, metadata and the
 * resource timing buffer (which catches anything the page fetched, including chunked players).
 */
NOVA.scanDocument = function (doc, pageUrl) {
    var found = [];
    var push = function (url, options) {
        var item = NOVA.toItem(url, Object.assign({ pageUrl: pageUrl }, options || {}));
        if (item) found.push(item);
    };

    if (!doc) return { items: [], unsupported: [] };

    try {
        doc.querySelectorAll('video, audio').forEach(function (element) {
            var isVideo = element.tagName.toLowerCase() === 'video';
            var sources = [];
            if (element.currentSrc) sources.push(element.currentSrc);
            if (element.getAttribute('src')) sources.push(element.getAttribute('src'));
            element.querySelectorAll('source').forEach(function (source) {
                var value = source.getAttribute('src');
                if (value) sources.push(value);
            });

            sources.forEach(function (source) {
                push(source, {
                    kind: isVideo ? 'video' : 'audio',
                    source: 'media-element',
                    duration: element.duration,
                    width: isVideo ? element.videoWidth : 0,
                    height: isVideo ? element.videoHeight : 0
                });
            });
        });
    } catch (e) { /* a malformed element must not break the scan */ }

    try {
        doc.querySelectorAll('a[href]').forEach(function (anchor) {
            var href = anchor.getAttribute('href');
            if (!href) return;
            var absolute;
            try { absolute = new URL(href, pageUrl || 'https://nova.invalid').href; } catch (e) { return; }
            var info = NOVA.classify(absolute);
            var hasDownload = anchor.hasAttribute('download');
            if (!info && !hasDownload) return;
            push(absolute, {
                kind: info ? info.kind : 'file',
                source: 'link',
                name: hasDownload ? (anchor.getAttribute('download') || '') : ''
            });
        });
    } catch (e) { /* ignore */ }

    try {
        doc.querySelectorAll('link[href]').forEach(function (link) {
            var rel = (link.getAttribute('rel') || '').toLowerCase();
            if (rel !== 'preload' && rel !== 'prefetch') return;
            var href = link.getAttribute('href');
            if (!href) return;
            var absolute;
            try { absolute = new URL(href, pageUrl || 'https://nova.invalid').href; } catch (e) { return; }
            var info = NOVA.classify(absolute, link.getAttribute('as'));
            if (!info) return;
            push(absolute, { kind: info.kind, source: 'preload' });
        });
    } catch (e) { /* ignore */ }

    try {
        doc.querySelectorAll('meta[property], meta[name]').forEach(function (meta) {
            var key = (meta.getAttribute('property') || meta.getAttribute('name') || '').toLowerCase();
            var value = meta.getAttribute('content');
            if (!value) return;
            if (key === 'og:video' || key === 'og:video:url' || key === 'og:video:secure_url' ||
                key === 'twitter:player:stream' || key === 'og:audio' || key === 'og:audio:url') {
                push(value, { kind: key.indexOf('audio') >= 0 ? 'audio' : 'video', source: 'meta' });
            }
        });
    } catch (e) { /* ignore */ }

    try {
        if (typeof performance !== 'undefined' && performance.getEntriesByType) {
            performance.getEntriesByType('resource').forEach(function (entry) {
                var info = NOVA.classify(entry.name);
                if (!info) return;
                push(entry.name, {
                    kind: info.kind,
                    source: 'network',
                    size: entry.transferSize || entry.encodedBodySize || 0,
                    duration: 0
                });
            });
        }
    } catch (e) { /* ignore */ }

    var merged = NOVA.mergeItems(found);
    return {
        items: merged.filter(function (item) { return !item.unsupported; }),
        unsupported: merged.filter(function (item) { return !!item.unsupported; })
    };
};

NOVA.unsupportedReason = function (item) {
    if (!item) return '';
    if (item.unsupported === 'stream') return NOVA.t('unsupportedStream');
    if (item.unsupported === 'blob') return NOVA.t('unsupportedBlob');
    if (item.unsupported === 'scheme') return NOVA.t('unsupportedScheme');
    return '';
};
