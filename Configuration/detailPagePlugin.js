/* global ApiClient, Dashboard, pluginManager, Events */

const pluginId = 'b2be5f82-6324-4e02-a66c-6da5a160ac45';

const MovieSplitterPlugin = {

    // ── Helpers ────────────────────────────────────────────────────────────

    async _getPluginConfig() {
        return ApiClient.getPluginConfiguration(pluginId);
    },

    async _splitItem(itemId, statusEl) {
        statusEl.textContent = 'Running…';
        statusEl.style.color = '';
        try {
            const url = ApiClient.getUrl('MovieSplitter/SplitItem', { itemId });
            const result = await ApiClient.ajax({ type: 'POST', url, dataType: 'json' });
            statusEl.textContent = `Done — ${result.EpisodesCreated} episode(s) created.`;
            statusEl.style.color = '#10b981';
        } catch (err) {
            const msg = err?.response ? await err.response.text() : String(err);
            statusEl.textContent = 'Error: ' + msg;
            statusEl.style.color = '#ef4444';
        }
    },

    _isMovie(item) {
        return item?.Type === 'Movie';
    },

    // ── 1. Detail page section ─────────────────────────────────────────────
    //
    // Injected below the main action buttons when a movie detail page opens.

    async onDetailPage(item, page) {
        if (!this._isMovie(item)) return;

        const config = await this._getPluginConfig();
        const detectorLabel = config.DetectorMode ?? 'Heuristic';
        const ollamaNote = config.OllamaEnabled ? ' + Ollama' : '';

        const section = document.createElement('div');
        section.className = 'detailSection';
        section.innerHTML = `
            <h3 class="detailSectionHeader">Movie Splitter</h3>
            <div style="
                padding: 14px 16px;
                background: rgba(124,58,237,0.1);
                border: 1px solid rgba(124,58,237,0.35);
                border-radius: 6px;
                margin: 0 0 1em;
            ">
                <p style="margin:0 0 8px;font-size:0.85em;opacity:0.75;">
                    Detector: ${detectorLabel}${ollamaNote}
                </p>
                <div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap;">
                    <button
                        is="emby-button"
                        class="emby-button button-submit raised"
                        id="btnSplitDetail"
                        style="background:#7c3aed;border:none;color:#fff;">
                        Split into episodes
                    </button>
                    <button
                        is="emby-button"
                        class="emby-button"
                        id="btnSplitSettings">
                        Settings
                    </button>
                    <span id="splitDetailStatus" style="font-size:0.85em;"></span>
                </div>
            </div>
        `;

        // Insert after the main detail action buttons
        const anchor = page.querySelector('.mainDetailButtons')
            ?? page.querySelector('.itemDetailGalleryLink')
            ?? page.querySelector('.detailPagePrimaryContent');

        if (anchor?.parentNode) {
            anchor.parentNode.insertBefore(section, anchor.nextSibling);
        } else {
            page.querySelector('.detailPageContent')?.appendChild(section);
        }

        section.querySelector('#btnSplitDetail').addEventListener('click', () => {
            const statusEl = section.querySelector('#splitDetailStatus');
            this._splitItem(item.Id, statusEl);
        });

        section.querySelector('#btnSplitSettings').addEventListener('click', () => {
            Dashboard.navigate('configurationpage?name=moviesplitter');
        });
    },

    // ── 2. Action button in the top button row ─────────────────────────────

    onDetailPageButtons(item, buttons) {
        if (!this._isMovie(item)) return;

        buttons.push({
            name: 'Split into episodes',
            icon: 'call_split',
            id: 'btnSplitRow',
            onClick: async (btn) => {
                const statusEl = document.createElement('span');
                statusEl.style.cssText = 'font-size:0.85em;margin-left:8px;';
                btn.parentNode?.insertBefore(statusEl, btn.nextSibling);
                btn.disabled = true;
                await this._splitItem(item.Id, statusEl);
                btn.disabled = false;
            }
        });
    },

    // ── 3. Context menu item ────────────────────────────────────────────────

    onContextMenu(item) {
        if (!this._isMovie(item)) return;

        const self = this;
        return {
            name: 'Split into episodes',
            icon: 'call_split',
            onClick() {
                // Confirmation toast in bottom-right corner
                const dialog = document.createElement('div');
                dialog.style.cssText = `
                    position:fixed;bottom:24px;right:24px;
                    background:#1e1e2e;color:#fff;
                    border:1px solid rgba(124,58,237,0.5);
                    border-radius:8px;padding:16px 20px;
                    z-index:9999;min-width:280px;
                    box-shadow:0 8px 32px rgba(0,0,0,0.5);
                `;
                dialog.innerHTML = `
                    <p style="margin:0 0 12px;font-weight:600;">
                        Split "${item.Name}" into episodes?
                    </p>
                    <div style="display:flex;gap:8px;align-items:center;">
                        <button id="dlgConfirm" style="
                            background:#7c3aed;color:#fff;border:none;
                            padding:7px 16px;border-radius:4px;cursor:pointer;font-size:13px;">
                            Run
                        </button>
                        <button id="dlgCancel" style="
                            background:transparent;color:#fff;
                            border:1px solid rgba(255,255,255,0.2);
                            padding:7px 16px;border-radius:4px;cursor:pointer;font-size:13px;">
                            Cancel
                        </button>
                        <span id="dlgStatus" style="font-size:12px;"></span>
                    </div>
                `;
                document.body.appendChild(dialog);

                dialog.querySelector('#dlgCancel').onclick = () => dialog.remove();
                dialog.querySelector('#dlgConfirm').onclick = async () => {
                    dialog.querySelector('#dlgConfirm').disabled = true;
                    dialog.querySelector('#dlgCancel').disabled = true;
                    const statusEl = dialog.querySelector('#dlgStatus');
                    await self._splitItem(item.Id, statusEl);
                    setTimeout(() => dialog.remove(), 3000);
                };
            }
        };
    },

    // ── Register with Jellyfin's pluginManager ─────────────────────────────

    register() {
        const self = this;

        pluginManager.register({
            type: 'itemDetailPage',

            async init(page, item) {
                await self.onDetailPage(item, page);
            },

            getAdditionalCommands(item) {
                const buttons = [];
                self.onDetailPageButtons(item, buttons);
                return buttons;
            }
        });

        pluginManager.register({
            type: 'itemContextMenu',

            visible(item) {
                return self._isMovie(item);
            },

            getOptions(item) {
                const opt = self.onContextMenu(item);
                return opt ? [opt] : [];
            }
        });

        // ── MutationObserver fallback ────────────────────────────────────────
        // Guards against Jellyfin versions where pluginManager hooks don't fire.
        // Watches for the detail page container and injects manually if needed.

        const observer = new MutationObserver(() => {
            const detailPage = document.querySelector('.itemDetailPage');
            if (!detailPage || detailPage.dataset.splitterInjected) return;
            detailPage.dataset.splitterInjected = '1';

            // Attempt to read item ID from the URL
            const params = new URLSearchParams(window.location.search);
            const itemId = params.get('id');
            if (!itemId) return;

            ApiClient.getItem(ApiClient.getCurrentUserId(), itemId).then(item => {
                if (self._isMovie(item)) self.onDetailPage(item, detailPage);
            }).catch(() => { });
        });

        observer.observe(document.body, { childList: true, subtree: true });
    }
};

MovieSplitterPlugin.register();
