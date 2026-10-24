/* NovaDownload — content script.
 *
 * Injected on demand (activeTab + scripting) when the user opens the popup, or by the service
 * worker for a context-menu action. It only reads the DOM it is injected into: no network calls,
 * no page modification.
 */

(function () {
    if (window.__novaDownloadContent) return;
    window.__novaDownloadContent = true;

    var respond = function (handler) {
        return function (message, sender, sendResponse) {
            try {
                sendResponse(handler(message));
            } catch (error) {
                sendResponse({ items: [], unsupported: [], error: String(error && error.message ? error.message : error) });
            }
            return true;
        };
    };

    NOVA.api.runtime.onMessage.addListener(respond(function (message) {
        if (!message || message.type !== 'nova:scan-here') return { items: [], unsupported: [] };

        var result = NOVA.scanDocument(document, location.href);

        // A player that has not started yet exposes an element but no URL: tell the user why the
        // list is empty instead of pretending nothing is there.
        var hasPlayer = false;
        try {
            document.querySelectorAll('video, audio').forEach(function (element) {
                hasPlayer = true;
                if (element.currentSrc || element.getAttribute('src')) return;
                if (element.querySelector('source[src]')) return;
                if (element.readyState === 0 && element.autoplay) hasPlayer = true;
            });
        } catch (error) { /* ignore */ }

        var blobCount = 0;
        try {
            document.querySelectorAll('video[src^="blob:"], audio[src^="blob:"], source[src^="blob:"]').forEach(function () {
                blobCount++;
            });
        } catch (error) { /* ignore */ }

        return {
            items: result.items,
            unsupported: result.unsupported,
            hasPlayer: hasPlayer,
            blobCount: blobCount,
            title: document.title,
            url: location.href
        };
    }));
})();
