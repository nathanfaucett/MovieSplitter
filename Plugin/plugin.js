(() => {
    const PLUGIN_ID = "b2be5f82-6324-4e02-a66c-6da5a160ac45";
    const BUTTON_CLASS = "btnMovieSplitter";

    console.log(`[${PLUGIN_ID}] Movie Splitter plugin script starting...`);

    let currentItemId = null;
    let observer = null;
    let debounceTimer = null;

    // ── Helpers ──────────────────────────────────────────────────────────────
    function getItemIdFromUrl() {
        const hash = window.location.hash;
        console.log(`[${PLUGIN_ID}] getItemIdFromUrl() - Current hash:`, hash);

        if (!hash.includes("/details")) {
            console.log(`[${PLUGIN_ID}] Not on a details page`);
            return null;
        }

        const params = new URLSearchParams(hash.split("?")[1] ?? "");
        const itemId = params.get("id");
        console.log(`[${PLUGIN_ID}] Extracted itemId:`, itemId);
        return itemId;
    }

    function createSplitButton() {
        console.log(`[${PLUGIN_ID}] Creating split button...`);
        const btn = document.createElement("button");
        btn.setAttribute("is", "emby-button");
        btn.setAttribute("type", "button");
        btn.setAttribute("title", "Split into episodes");
        btn.className = `button-flat detailButton emby-button ${BUTTON_CLASS}`;
        btn.innerHTML = `
            <div class="detailButton-content">
                <span class="material-icons detailButton-icon call_split" aria-hidden="true"></span>
            </div>`;
        console.log(`[${PLUGIN_ID}] Split button created successfully`);
        return btn;
    }

    async function runSplit(itemId, btn, targetEpisodeMinutesValue) {
        /** @type {number | undefined} */
        let targetEpisodeMinutes;
        if (targetEpisodeMinutesValue) {
            targetEpisodeMinutes = Number.parseInt(targetEpisodeMinutesValue, 10);
            if (Number.isNaN(targetEpisodeMinutes) || targetEpisodeMinutes <= 0) {
                Dashboard.alert("Please enter a valid number of minutes.");
                return;
            }
        }
        const originalTitle = btn.title;
        btn.disabled = true;
        btn.title = "Splitting…";
        const icon = btn.querySelector(".material-icons");
        if (icon) icon.textContent = "hourglass_empty";

        console.log(`[${PLUGIN_ID}] runSplit() called for itemId:`, itemId);

        try {
            console.log(`[${PLUGIN_ID}] Sending split request...`);
            const url = ApiClient.getUrl("MovieSplitter/SplitItem", { itemId, targetEpisodeMinutes });
            console.log(`[${PLUGIN_ID}] API URL:`, url);

            const result = await ApiClient.ajax({
                type: "POST",
                url,
                dataType: "json",
            });
            console.log(`[${PLUGIN_ID}] API response:`, result);

            if (result.message) Dashboard.alert(result.message);
            else
                Dashboard.alert(
                    `Done! Created ${result.episodesCreated} episode file(s).`,
                );
        } catch (err) {
            console.error(`[${PLUGIN_ID}] Split failed:`, err);
            const msg = err?.responseJSON?.error ?? err?.message ?? "Unknown error";
            Dashboard.alert(`Split failed: ${msg}`);
        } finally {
            btn.disabled = false;
            btn.title = originalTitle;
            if (icon) icon.textContent = "call_split";
        }
    }

    function confirmAndSplit(itemId, btn) {
        console.log(`[${PLUGIN_ID}] confirmAndSplit() triggered`);
        Dashboard.confirm(
            `<p>Split this movie into individual episode files using subtitle analysis?</p>
            <div class="inputContainer" style="text-align: left;">
                <label class="inputLabel" for="targetEpisodeMinutes">Target episode length (minutes)</label>
                <input type="number" id="targetEpisodeMinutes" placeholder="Configured value" class="emby-input" min="1" max="120" step="1" />
            </div>`,
            "Split into episodes",
            (confirmed) => {
                /** @type {string | undefined} */
                const targetEpisodeMinutes = document.getElementById("targetEpisodeMinutes")?.value;
                console.log(`[${PLUGIN_ID}] User confirmed:`, confirmed);
                if (confirmed) runSplit(itemId, btn, targetEpisodeMinutes);
            },
        );
    }

    // ── Core Functions ─────────────────────────────────────────────────────
    function unmountButtons() {
        const count = document.querySelectorAll(`.${BUTTON_CLASS}`).length;
        console.log(`[${PLUGIN_ID}] unmountButtons() - Removing ${count} buttons`);
        for (const el of document.querySelectorAll(`.${BUTTON_CLASS}`)) {
            el.remove();
        }
    }

    function addButtonToRow(buttonRow, itemId) {
        if (buttonRow.querySelector(`.${BUTTON_CLASS}`)) {
            console.log(`[${PLUGIN_ID}] Button already present in this row`);
            return false;
        }

        console.log(`[${PLUGIN_ID}] Adding button to row`);
        const btn = createSplitButton();
        btn.addEventListener("click", () => confirmAndSplit(itemId, btn));

        const moreBtn = buttonRow.querySelector(".btnMoreCommands");
        if (moreBtn) {
            buttonRow.insertBefore(btn, moreBtn);
            console.log(`[${PLUGIN_ID}] ✅ Inserted before .btnMoreCommands`);
        } else {
            buttonRow.appendChild(btn);
            console.log(`[${PLUGIN_ID}] ✅ Appended to row`);
        }
        return true;
    }

    function mountAllVisibleButtons() {
        const itemId = getItemIdFromUrl();
        if (!itemId) return;

        if (itemId !== currentItemId) {
            console.log(`[${PLUGIN_ID}] Item changed, resetting`);
            currentItemId = itemId;
            unmountButtons();
        }

        const buttonRows = document.querySelectorAll(".mainDetailButtons");
        console.log(
            `[${PLUGIN_ID}] mountAllVisibleButtons → Found ${buttonRows.length} rows`,
        );

        let addedAny = false;
        for (const row of buttonRows) {
            if (addButtonToRow(row, itemId)) {
                addedAny = true;
            }
        }

        return addedAny;
    }

    // ── Debounced Observer ─────────────────────────────────────────────────
    function startObserver() {
        if (observer) return;

        observer = new MutationObserver(() => {
            if (debounceTimer) clearTimeout(debounceTimer);

            debounceTimer = setTimeout(() => {
                console.log(
                    `[${PLUGIN_ID}] DOM changed → checking for new .mainDetailButtons`,
                );
                const added = mountAllVisibleButtons();

                // Optional: Disconnect observer if we successfully added buttons
                // (uncomment if you want to reduce noise)
                // if (added) observer.disconnect();
            }, 120);
        });

        observer.observe(document.body, { childList: true, subtree: true });
        console.log(`[${PLUGIN_ID}] MutationObserver started (debounced)`);
    }

    // ── Navigation ─────────────────────────────────────────────────────────
    function handleHashChange() {
        console.log(`[${PLUGIN_ID}] handleHashChange() triggered`);
        currentItemId = null; // Force refresh
        unmountButtons();
        setTimeout(() => {
            mountAllVisibleButtons();
            startObserver();
        }, 100);
    }

    // ── Init ───────────────────────────────────────────────────────────────
    function init() {
        console.log(`[${PLUGIN_ID}] Initializing plugin...`);
        console.log(`[${PLUGIN_ID}] ApiClient:`, typeof ApiClient !== "undefined");
        console.log(`[${PLUGIN_ID}] Dashboard:`, typeof Dashboard !== "undefined");

        handleHashChange();
        window.addEventListener("hashchange", handleHashChange);
        window.addEventListener("popstate", handleHashChange);

        setTimeout(handleHashChange, 700);
    }

    init();
})();
