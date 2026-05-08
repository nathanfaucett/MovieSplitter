import pluginManager from 'pluginManager';
import ApiClient from 'apiClient';

const PLUGIN_ID = 'b2be5f82-6324-4e02-a66c-6da5a160ac45';

function splitItem(itemId, statusEl) {
    statusEl.textContent = 'Running\u2026';
    statusEl.style.color = '';

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('MovieSplitter/SplitItem', { itemId: itemId }),
        dataType: 'json'
    }).then((result) => {
        statusEl.textContent =
            `Done \u2014 ${result.EpisodesCreated || 0} episode(s) created.`;
        statusEl.style.color = 'var(--color-text-success, #10b981)';
    }).catch((err) => {
        statusEl.textContent =
            `Error: ${err?.message ? err.message : String(err)}`;
        statusEl.style.color = 'var(--color-text-danger, #ef4444)';
    });
}

// ── pluginManager hook (Jellyfin 10.9 itemdetailoptions API) ────────────────

pluginManager.register({
    type: 'itemdetailoptions',

    visible: (options) => options?.item && options.item.Type === 'Movie',

    getOptionHtml: (/* options */) => [
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
    ].join('\n'),

    bindEvents: (options) => {
        const item = options.item;
        const el = options.element;
        const btn = el.querySelector('#btnMovieSplitterRun');
        const statusEl = el.querySelector('#movieSplitterStatus');

        if (!btn || !statusEl) {
            return;
        }

        btn.addEventListener('click', () => {
            btn.disabled = true;
            splitItem(item.Id, statusEl);
            setTimeout(() => { btn.disabled = false; }, 5000);
        });
    }
});
