(() => {
    const PLUGIN_ID = 'b2be5f82-6324-4e02-a66c-6da5a160ac45';
    const BUTTON_ID = 'btnMovieSplitter';

    // ── Helpers ──────────────────────────────────────────────────────────────

    function getItemIdFromUrl() {
        const params = new URLSearchParams(window.location.hash.split('?')[1] ?? '');
        return params.get('id');
    }

    function createSplitButton() {
        const btn = document.createElement('button');
        btn.id = BUTTON_ID;
        btn.setAttribute('is', 'emby-button');
        btn.setAttribute('type', 'button');
        btn.setAttribute('title', 'Split into episodes');
        btn.className = 'button-flat detailButton emby-button';
        btn.innerHTML = `
            <div class="detailButton-content">
                <span class="material-icons detailButton-icon call_split" aria-hidden="true"></span>
            </div>`;
        return btn;
    }

    async function runSplit(itemId, btn) {
        const originalTitle = btn.title;

        btn.disabled = true;
        btn.title = 'Splitting…';
        const icon = btn.querySelector('.material-icons');
        if (icon) icon.textContent = 'hourglass_empty';

        try {
            const url = ApiClient.getUrl('MovieSplitter/SplitItem', { itemId });
            const result = await ApiClient.ajax({
                type: 'POST', url, dataType: 'json'
            });

            if (result.message) {
                Dashboard.alert(result.message);
            } else {
                Dashboard.alert(`Done! Created ${result.episodesCreated} episode file(s).`);
            }
        } catch (err) {
            const msg = err?.responseJSON?.error ?? err?.message ?? 'Unknown error';
            Dashboard.alert(`Split failed: ${msg}`);
        } finally {
            btn.disabled = false;
            btn.title = originalTitle;
            if (icon) icon.textContent = 'call_split';
        }
    }

    function confirmAndSplit(itemId, btn) {
        Dashboard.confirm(
            'Split this movie into individual episode files using subtitle analysis?',
            'Split into episodes',
            confirmed => { if (confirmed) runSplit(itemId, btn); }
        );
    }

    // ── Injection ────────────────────────────────────────────────────────────

    function injectButton(buttonRow) {
        if (buttonRow.querySelector(`#${BUTTON_ID}`)) return; // already injected

        const itemId = getItemIdFromUrl();
        if (!itemId) return;

        const btn = createSplitButton();
        btn.addEventListener('click', () => confirmAndSplit(itemId, btn));

        // Insert before the "More" button (btnMoreCommands) if it exists,
        // otherwise just append.
        const moreBtn = buttonRow.querySelector('.btnMoreCommands');
        if (moreBtn) {
            buttonRow.insertBefore(btn, moreBtn);
        } else {
            buttonRow.appendChild(btn);
        }
    }

    // ── Observer ─────────────────────────────────────────────────────────────

    function tryInject() {
        const buttonRow = document.querySelector('.mainDetailButtons');
        if (buttonRow) injectButton(buttonRow);
    }

    // Run once immediately in case the page is already rendered.
    tryInject();

    // Re-run whenever the hash changes (Jellyfin is a SPA).
    window.addEventListener('hashchange', () => {
        // Give the SPA a moment to render the new page.
        setTimeout(tryInject, 500);
    });

    // Also observe DOM mutations so we catch the initial render.
    const observer = new MutationObserver(() => tryInject());
    observer.observe(document.body, { childList: true, subtree: true });
})();
