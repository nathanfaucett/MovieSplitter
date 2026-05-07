/* global ApiClient, Dashboard, pluginManager, Events */

const MovieSplitterPlugin = {

    // ── Helpers ────────────────────────────────────────────────────────────

    async _getPluginConfig() {
        return ApiClient.getPluginConfiguration('b2be5f82-6324-4e02-a66c-6da5a160ac45');
    },

    async _splitItem(itemId, statusEl) {
        statusEl.textContent = 'Running\u2026';
        statusEl.style.color = '';
        try {
            const url = ApiClient.getUrl('MovieSplitter/SplitItem', { itemId });
            const result = await ApiClient.ajax({ type: 'POST', url, dataType: 'json' });
            statusEl.textContent = 'Done \u2014 ' + result.EpisodesCreated + ' episode(s) created.';
            statusEl.style.color = '#10b981';
        } catch (err) {
            const msg = err && err.response ? (err.response.text ? err.response.text() : String(err)) : String(err);
            statusEl.textContent = 'Error: ' + (typeof msg === 'string' ? msg : 'Unknown error');
            statusEl.style.color = '#ef4444';
        }
    },

    _isMovie(item) {
        return item && item.Type === 'Movie';
    },

    // ── 1. Detail page section ─────────────────────────────────────────────
    //
    // Injected below the main action buttons when a movie detail page opens.
    // All DOM manipulation uses createElement instead of innerHTML to avoid
    // Jellyfin's translateHtml pipeline receiving non-string values.

    async onDetailPage(item, page) {
        if (!this._isMovie(item)) return;

        let config;
        try {
            config = await this._getPluginConfig();
        } catch (e) {
            config = {};
        }

        const detectorLabel = String(config.DetectorMode || 'Heuristic');
        const ollamaNote = config.OllamaEnabled ? ' + Ollama' : '';

        // Build DOM manually to stay out of Jellyfin's translateHtml pipeline
        const section = document.createElement('div');
        section.className = 'detailSection';

        const heading = document.createElement('h3');
        heading.className = 'detailSectionHeader';
        heading.textContent = 'Movie Splitter';
        section.appendChild(heading);

        const panel = document.createElement('div');
        panel.style.cssText = 'padding:14px 16px;background:rgba(124,58,237,0.1);border:1px solid rgba(124,58,237,0.35);border-radius:6px;margin:0 0 1em;';

        const note = document.createElement('p');
        note.style.cssText = 'margin:0 0 8px;font-size:0.85em;opacity:0.75;';
        note.textContent = 'Detector: ' + detectorLabel + ollamaNote;
        panel.appendChild(note);

        const row = document.createElement('div');
        row.style.cssText = 'display:flex;gap:10px;align-items:center;flex-wrap:wrap;';

        const btnRun = document.createElement('button');
        btnRun.setAttribute('is', 'emby-button');
        btnRun.className = 'emby-button button-submit raised';
        btnRun.style.cssText = 'background:#7c3aed;border:none;color:#fff;';
        btnRun.textContent = 'Split into episodes';

        const btnSettings = document.createElement('button');
        btnSettings.setAttribute('is', 'emby-button');
        btnSettings.className = 'emby-button';
        btnSettings.textContent = 'Settings';

        const statusEl = document.createElement('span');
        statusEl.style.cssText = 'font-size:0.85em;';

        row.appendChild(btnRun);
        row.appendChild(btnSettings);
        row.appendChild(statusEl);
        panel.appendChild(row);
        section.appendChild(panel);

        const anchor = page.querySelector('.mainDetailButtons')
            || page.querySelector('.itemDetailGalleryLink')
            || page.querySelector('.detailPagePrimaryContent');

        if (anchor && anchor.parentNode) {
            anchor.parentNode.insertBefore(section, anchor.nextSibling);
        } else {
            const content = page.querySelector('.detailPageContent');
            if (content) content.appendChild(section);
        }

        const self = this;
        btnRun.addEventListener('click', function () {
            self._splitItem(item.Id, statusEl);
        });
        btnSettings.addEventListener('click', function () {
            Dashboard.navigate('configurationpage?name=moviesplitter');
        });
    },

    // ── 2. Context menu item ────────────────────────────────────────────────
    // NOTE: getAdditionalCommands / onDetailPageButtons is intentionally
    // omitted — Jellyfin passes those button objects through translateHtml,
    // which calls .indexOf on them expecting a string and throws
    // "e.indexOf is not a function".  The detail-page section above already
    // provides a prominent Run button, so the top-row button is not needed.

    onContextMenu(item) {
        if (!this._isMovie(item)) return null;

        const self = this;
        return {
            name: 'Split into episodes',
            icon: 'call_split',
            onClick: function () {
                const dialog = document.createElement('div');
                dialog.style.cssText = 'position:fixed;bottom:24px;right:24px;background:#1e1e2e;color:#fff;border:1px solid rgba(124,58,237,0.5);border-radius:8px;padding:16px 20px;z-index:9999;min-width:280px;box-shadow:0 8px 32px rgba(0,0,0,0.5);';

                const title = document.createElement('p');
                title.style.cssText = 'margin:0 0 12px;font-weight:600;';
                title.textContent = 'Split "' + (item.Name || 'this movie') + '" into episodes?';

                const btnRow = document.createElement('div');
                btnRow.style.cssText = 'display:flex;gap:8px;align-items:center;';

                const confirm = document.createElement('button');
                confirm.style.cssText = 'background:#7c3aed;color:#fff;border:none;padding:7px 16px;border-radius:4px;cursor:pointer;font-size:13px;';
                confirm.textContent = 'Run';

                const cancel = document.createElement('button');
                cancel.style.cssText = 'background:transparent;color:#fff;border:1px solid rgba(255,255,255,0.2);padding:7px 16px;border-radius:4px;cursor:pointer;font-size:13px;';
                cancel.textContent = 'Cancel';

                const dlgStatus = document.createElement('span');
                dlgStatus.style.fontSize = '12px';

                btnRow.appendChild(confirm);
                btnRow.appendChild(cancel);
                btnRow.appendChild(dlgStatus);
                dialog.appendChild(title);
                dialog.appendChild(btnRow);
                document.body.appendChild(dialog);

                cancel.onclick = function () { dialog.remove(); };
                confirm.onclick = async function () {
                    confirm.disabled = true;
                    cancel.disabled = true;
                    await self._splitItem(item.Id, dlgStatus);
                    setTimeout(function () { dialog.remove(); }, 3000);
                };
            }
        };
    },

    // ── Register with Jellyfin's pluginManager ─────────────────────────────

    register() {
        const self = this;

        pluginManager.register({
            type: 'itemDetailPage',

            init: async function (page, item) {
                await self.onDetailPage(item, page);
            }

            // getAdditionalCommands deliberately omitted — see note above.
        });

        pluginManager.register({
            type: 'itemContextMenu',

            visible: function (item) {
                return self._isMovie(item);
            },

            getOptions: function (item) {
                const opt = self.onContextMenu(item);
                return opt ? [opt] : [];
            }
        });

        // ── MutationObserver fallback ────────────────────────────────────────
        // Guards against Jellyfin versions where pluginManager hooks don't fire.

        const observer = new MutationObserver(function () {
            const detailPage = document.querySelector('.itemDetailPage');
            if (!detailPage || detailPage.dataset.splitterInjected) return;
            detailPage.dataset.splitterInjected = '1';

            const params = new URLSearchParams(window.location.search);
            const itemId = params.get('id');
            if (!itemId) return;

            ApiClient.getItem(ApiClient.getCurrentUserId(), itemId).then(function (item) {
                if (self._isMovie(item)) self.onDetailPage(item, detailPage);
            }).catch(function () { });
        });

        observer.observe(document.body, { childList: true, subtree: true });
    }
};

MovieSplitterPlugin.register();
