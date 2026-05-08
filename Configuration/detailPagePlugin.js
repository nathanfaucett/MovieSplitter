/* Movie Splitter – detail page button injection
*
* Jellyfin's web client is an ES module bundle. pluginManager, ApiClient, etc.
* are NOT window globals — they must be imported from the client's module
* system at runtime.
*
* Compatible with Jellyfin 10.9+
*/

import pluginManager from 'pluginManager';
import ApiClient from 'apiClient';

const PLUGIN_ID = 'b2be5f82-6324-4e02-a66c-6da5a160ac45';

// ── Utility: call the split API ─────────────────────────────────────────────

function splitItem(itemId, statusEl) {
    statusEl.textContent = 'Running\u2026';
    statusEl.style.color = '';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('MovieSplitter/SplitItem', { itemId: itemId }),
        dataType: 'json'
    }).then(function (result) {
        statusEl.textContent =
            'Done \u2014 ' + (result.EpisodesCreated || 0) + ' episode(s) created.';
        statusEl.style.color = 'var(--color-text-success, #10b981)';
    }).catch(function (err) {
        statusEl.textContent =
            'Error: ' + (err && err.message ? err.message : String(err));
        statusEl.style.color = 'var(--color-text-danger, #ef4444)';
    });
}

// ── pluginManager hook (Jellyfin 10.9 itemdetailoptions API) ────────────────

pluginManager.register({
    type: 'itemdetailoptions',

    visible: function (options) {
        return options && options.item && options.item.Type === 'Movie';
    },

    getOptionHtml: function (/* options */) {
        return [
            '<div class="verticalSection" id="movieSplitterSection">',
            ' <h3 class="sectionTitle">Movie Splitter</h3>',
            ' <div style="display:flex;gap:.75em;align-items:center;flex-wrap:wrap;margin-top:.5em;">',
            ' <button is="emby-button"',
            ' class="emby-button raised button-submit"',
            ' id="btnMovieSplitterRun">',
            ' Split into episodes',
            ' </button>',
            ' <span id="movieSplitterStatus" style="font-size:.85em;"></span>',
            ' </div>',
            '</div>'
        ].join('\n');
    },

    bindEvents: function (options) {
        var item = options.item;
        var el = options.element;
        var btn = el.querySelector('#btnMovieSplitterRun');
        var statusEl = el.querySelector('#movieSplitterStatus');

        if (!btn || !statusEl) return;

        btn.addEventListener('click', function () {
            btn.disabled = true;
            splitItem(item.Id, statusEl);
            setTimeout(function () { btn.disabled = false; }, 5000);
        });
    }
});
