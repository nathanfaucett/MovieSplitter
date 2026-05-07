/* global ApiClient, pluginManager */

(function () {
    'use strict';

    const PLUGIN_ID = 'b2be5f82-6324-4e02-a66c-6da5a160ac45';

    // ── Split helper ────────────────────────────────────────────────────────

    function splitItem(itemId, statusEl) {
        statusEl.textContent = 'Running\u2026';
        statusEl.style.color = '';

        var url = ApiClient.getUrl('MovieSplitter/SplitItem', { itemId: itemId });
        ApiClient.ajax({ type: 'POST', url: url, dataType: 'json' })
            .then(function (result) {
                statusEl.textContent = 'Done \u2014 ' + result.EpisodesCreated + ' episode(s) created.';
                statusEl.style.color = 'var(--color-text-success, #10b981)';
            })
            .catch(function (err) {
                statusEl.textContent = 'Error: ' + (err && err.message ? err.message : String(err));
                statusEl.style.color = 'var(--color-text-danger, #ef4444)';
            });
    }

    // ── itemdetailoptions plugin ────────────────────────────────────────────
    // This is the supported hook in Jellyfin 10.9+ for adding custom sections
    // to the movie/item detail page. Jellyfin calls getOptionHtml() to get
    // markup to render, then calls bindEvents() after it is inserted into DOM.

    pluginManager.register({
        type: 'itemdetailoptions',

        // Only show for movies
        visible: function (options) {
            return options && options.item && options.item.Type === 'Movie';
        },

        // Return an HTML string for the panel Jellyfin will insert.
        // Keep it simple — a labelled section with one button and a status span.
        getOptionHtml: function (options) {
            return [
                '<div class="verticalSection">',
                '  <h3 class="sectionTitle">Movie Splitter</h3>',
                '</div>',
                '<div class="verticalSection verticalSection-extrabottompadding">',
                '  <div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap;">',
                '    <button is="emby-button" class="emby-button raised button-submit" id="btnMovieSplitterRun">',
                '      Split into episodes',
                '    </button>',
                '    <span id="movieSplitterStatus" style="font-size:0.85em;"></span>',
                '  </div>',
                '</div>'
            ].join('');
        },

        // Called by Jellyfin after the HTML has been injected into the page.
        bindEvents: function (options) {
            var item = options.item;
            var btn = options.element.querySelector('#btnMovieSplitterRun');
            var statusEl = options.element.querySelector('#movieSplitterStatus');

            if (!btn || !statusEl) return;

            btn.addEventListener('click', function () {
                btn.disabled = true;
                splitItem(item.Id, statusEl);
                setTimeout(function () { btn.disabled = false; }, 4000);
            });
        }
    });

})();
