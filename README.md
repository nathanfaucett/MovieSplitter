# Movie Splitter — Jellyfin Plugin

Splits a single movie file that contains multiple joined episodes into individual episode files. Uses subtitle cues for boundary detection, with optional Ollama LLM support for smarter analysis.

---

## Table of contents

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Building from source](#building-from-source)
- [Release flow](#release-flow)
- [Installing on a Jellyfin server](#installing-on-a-jellyfin-server)
- [Configuration](#configuration)
- [Detection modes](#detection-modes)
- [Triggering a split](#triggering-a-split)
- [Output](#output)
- [API reference](#api-reference)
- [Project structure](#project-structure)
- [Troubleshooting](#troubleshooting)

---

## How it works

Some movies are actually miniseries or multi-episode compilations released as a single file — common with older TV recordings, foreign releases, or fan-created archives. This plugin detects where one episode ends and the next begins by analysing subtitle cues, then uses ffmpeg to losslessly split the file at those points.

The detection pipeline is:

1. **Subtitle loading** — finds an external `.srt`/`.ass` sidecar file, or extracts an embedded subtitle track via ffmpeg
2. **Boundary detection** — one of three strategies (see [Detection modes](#detection-modes))
3. **Splitting** — ffmpeg remuxes each segment with `-c copy` (no re-encoding, no quality loss)
4. **Library update** — triggers a Jellyfin library scan so the new episode files appear immediately

---

## Requirements

| Dependency | Version    | Notes                                                                              |
| ---------- | ---------- | ---------------------------------------------------------------------------------- |
| .NET SDK   | 8.0+       | For building                                                                       |
| Jellyfin   | 10.9.0+    | Server target                                                                      |
| ffmpeg     | Any recent | Must be on the server's `PATH`, or Jellyfin's bundled ffmpeg is used automatically |
| Ollama     | Any        | Optional — only needed for Ollama/Composite detection modes                        |

The movie being split **must have subtitles** — either as an external sidecar file or as an embedded track. Movies with no subtitle data will be skipped with a warning in the Jellyfin log.

---

## Building from source

```bash
git clone https://github.com/your-org/jellyfin-plugin-moviesplitter
cd jellyfin-plugin-moviesplitter
dotnet build -c Release
```

The build output will be in:

```
bin/Release/net8.0/
├── MovieSplitter.dll
└── MovieSplitter.pdb       ← optional, include for readable stack traces
```

You only need `MovieSplitter.dll` for deployment. The embedded resources (config page HTML, client JS) are compiled directly into the DLL.

---

## Release flow

### 1. Bump the version

Version is set in `MovieSplitter.csproj`. Jellyfin uses this to detect upgrades:

```xml
<PropertyGroup>
  <Version>1.0.0.0</Version>
  <!-- format: Major.Minor.Patch.Build -->
</PropertyGroup>
```

| Change type                        | Example               |
| ---------------------------------- | --------------------- |
| Bug fix                            | `1.0.0.0` → `1.0.1.0` |
| New feature (backwards-compatible) | `1.0.0.0` → `1.1.0.0` |
| Breaking change or major rewrite   | `1.0.0.0` → `2.0.0.0` |

### 2. Build the release DLL

```bash
dotnet build -c Release
```

### 3. Create the release zip

Jellyfin expects plugin releases as a zip with the DLL at the top level (not nested in a subfolder):

```bash
cd bin/Release/net8.0
zip MovieSplitter_1.0.0.0.zip MovieSplitter.dll
```

Name the zip `PluginName_Version.zip` — Jellyfin's plugin catalogue uses this convention.

### 4. Generate the checksum

Jellyfin verifies plugin zips using an MD5 checksum stored in the repository manifest:

```bash
# Linux / macOS
md5sum MovieSplitter_1.0.0.0.zip

# Windows PowerShell
Get-FileHash MovieSplitter_1.0.0.0.zip -Algorithm MD5 | Select-Object Hash
```

### 5. Update the plugin manifest

If you are hosting a plugin catalogue (for one-click install from the Jellyfin UI), update `manifest.json` in your catalogue repository:

```json
[
    {
        "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
        "name": "Movie Splitter",
        "description": "Splits multi-episode movie files into individual episodes using subtitle analysis.",
        "overview": "Splits a single movie file containing multiple joined episodes into individual files.",
        "owner": "your-org",
        "category": "General",
        "versions": [
            {
                "version": "1.0.0.0",
                "changelog": "Initial release.",
                "targetAbi": "10.9.0.0",
                "sourceUrl": "https://github.com/your-org/jellyfin-plugin-moviesplitter/releases/download/v1.0.0.0/MovieSplitter_1.0.0.0.zip",
                "checksum": "<md5 from step 4>",
                "timestamp": "2025-01-01T00:00:00Z"
            }
        ]
    }
]
```

Key fields:

| Field       | Description                                                                |
| ----------- | -------------------------------------------------------------------------- |
| `guid`      | Must match `Plugin.Id` in `Plugin.cs` — never change this between releases |
| `targetAbi` | Minimum Jellyfin version required                                          |
| `sourceUrl` | Direct download URL to the release zip                                     |
| `checksum`  | MD5 of the zip (not the DLL)                                               |

### 6. Create a GitHub release

```bash
git tag v1.0.0.0
git push origin v1.0.0.0
```

Create a release on GitHub and attach `MovieSplitter_1.0.0.0.zip` as a release asset. The `sourceUrl` in your manifest should point to this asset's download URL.

---

## Installing on a Jellyfin server

There are two installation methods: via the plugin catalogue (easier, gets upgrades automatically) or manually (works without a catalogue).

### Method A — Plugin catalogue (recommended)

**1. Add the plugin repository**

In the Jellyfin web UI, go to:

```
Dashboard → Plugins → Repositories → + (add)
```

Enter the URL of the `manifest.json` file for this plugin, then click **Save**.

**2. Install the plugin**

```
Dashboard → Plugins → Catalogue → find "Movie Splitter" → Install
```

**3. Restart Jellyfin**

Plugin DLLs are loaded at startup. A restart is required after installing or upgrading.

```bash
# systemd
sudo systemctl restart jellyfin

# Docker
docker restart jellyfin
```

---

### Method B — Manual installation

Use this if you built from source or are not using a plugin catalogue.

**1. Locate the Jellyfin plugin directory**

| Platform        | Default path                                             |
| --------------- | -------------------------------------------------------- |
| Linux (systemd) | `/var/lib/jellyfin/plugins/`                             |
| Linux (Docker)  | Mapped volume — check your `docker run` or `compose.yml` |
| Windows         | `%PROGRAMDATA%\Jellyfin\Server\plugins\`                 |
| macOS           | `~/.local/share/jellyfin/plugins/`                       |

**2. Create a folder for the plugin**

Jellyfin expects each plugin in its own subdirectory named `PluginName_Version`:

```bash
mkdir -p /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0
```

**3. Copy the DLL**

```bash
cp bin/Release/net8.0/MovieSplitter.dll \
   /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/
```

Your plugin directory should look like this:

```
/var/lib/jellyfin/plugins/
└── MovieSplitter_1.0.0.0/
    └── MovieSplitter.dll
```

**4. Set permissions** (Linux only)

```bash
chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/
chmod 755 /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/
chmod 644 /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/MovieSplitter.dll
```

**5. Restart Jellyfin**

```bash
sudo systemctl restart jellyfin
```

**6. Verify the plugin loaded**

```
Dashboard → Plugins → My Plugins
```

"Movie Splitter" should appear with status **Active**. If it shows **Restart required**, restart the server once more.

---

### Upgrading

**Via catalogue:** Dashboard → Plugins → My Plugins → Movie Splitter → Update (if available).

**Manually:**

1. Stop Jellyfin: `sudo systemctl stop jellyfin`
2. Delete the old plugin folder: `rm -rf /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/`
3. Create the new versioned folder and copy the new DLL (repeat steps 2–5 above)
4. Start Jellyfin: `sudo systemctl start jellyfin`

Do **not** overwrite the DLL in-place while Jellyfin is running — the runtime holds a file lock on loaded assemblies.

---

### Uninstalling

**Via catalogue:** Dashboard → Plugins → My Plugins → Movie Splitter → Uninstall, then restart.

**Manually:**

```bash
sudo systemctl stop jellyfin
rm -rf /var/lib/jellyfin/plugins/MovieSplitter_1.0.0.0/
sudo systemctl start jellyfin
```

Plugin configuration is stored separately and will not be deleted automatically. To remove it too:

```bash
rm /var/lib/jellyfin/plugins/configurations/MovieSplitter.xml
```

---

## Configuration

Open **Dashboard → Plugins → My Plugins → Movie Splitter → Settings**.

### Detection

| Setting        | Default     | Description                             |
| -------------- | ----------- | --------------------------------------- |
| Detection mode | `Heuristic` | See [Detection modes](#detection-modes) |

### Heuristic settings

| Setting                | Default                                                   | Description                                                                                            |
| ---------------------- | --------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| Silence gap threshold  | `30` s                                                    | Gap between subtitle cues that signals an episode boundary                                             |
| Minimum episode length | `10` min                                                  | Prevents very short segments from being treated as episodes                                            |
| Cue word patterns      | `previously on,next time on,\bchapter \d+\b,\bpart \d+\b` | Comma-separated regex patterns — a subtitle line matching any of these is treated as a boundary marker |

### Ollama settings

| Setting           | Default                  | Description                               |
| ----------------- | ------------------------ | ----------------------------------------- |
| Enable Ollama     | Off                      | Must be on for Ollama and Composite modes |
| Ollama server URL | `http://localhost:11434` | Base URL — can be a remote host           |
| Model             | `llama3`                 | Any model installed in Ollama             |

Use the **Test connection** button to verify Ollama is reachable before enabling it.

### Output settings

| Setting           | Default    | Description                                                                         |
| ----------------- | ---------- | ----------------------------------------------------------------------------------- |
| Output subfolder  | `Episodes` | Created next to the source movie file                                               |
| Subtitle language | `eng`      | ISO 639-2 code used when searching for sidecar subtitle files, e.g. `Movie.eng.srt` |

---

## Detection modes

### Heuristic (default)

Finds boundaries using two signals in the subtitle data:

- **Silence gaps** — stretches with no subtitle cues longer than the configured threshold
- **Cue-word patterns** — subtitle lines matching phrases like "Previously on…" or "Chapter 1"

No external dependencies. Fast and deterministic. Works well for content that has clear textual episode markers.

### Ollama

Sends subtitle cues to a local Ollama LLM in overlapping windows of 120 lines. The model is prompted to identify episode boundaries and return a JSON array of timestamps. Results are validated and filtered before use.

If the Ollama server is unreachable or returns unparseable output, the detector automatically falls back to Heuristic — the task will never fail because of a network error.

Recommended models:

| Model     | Speed     | Notes                                |
| --------- | --------- | ------------------------------------ |
| `llama3`  | Medium    | Best general narrative understanding |
| `mistral` | Fast      | Solid instruction-following          |
| `phi3`    | Very fast | Best choice for low-resource servers |

### Composite

Runs both Heuristic and Ollama in sequence, then merges the results — collapsing timestamps within 15 seconds of each other into a single boundary. Best accuracy at the cost of the additional time required for the LLM pass.

---

## Triggering a split

### From the movie detail page

Open any movie in the Jellyfin web UI. The plugin injects three entry points:

- **Top button row** — a "Split into episodes" button alongside Play and Watchlist
- **Movie Splitter panel** — a section below the main buttons showing the active detector mode, with Run and Settings buttons
- **Context menu** — a "Split into episodes" entry in the ⋮ menu on both card view and the detail page

Each of these shows a confirmation step and inline progress status without navigating away from the page.

### From the plugin settings page

Paste a movie's Jellyfin item ID into the "Split a single movie" field and click **Split this movie**. The item ID appears in the URL when you open a movie: `…/details?id=abc123`.

### From Scheduled Tasks

```
Dashboard → Scheduled Tasks → Movie Splitter → Split Movies into Episodes
```

Click the play button to process all movies in the library. Jellyfin shows a native progress bar. Use this for bulk processing after initial configuration.

---

## Output

For a movie `The Long Film.mkv` with 3 detected boundaries:

```
The Long Film/
└── Episodes/
    ├── The Long Film - S01E01.mkv
    ├── The Long Film - S01E02.mkv
    ├── The Long Film - S01E03.mkv
    └── The Long Film - S01E04.mkv
```

Each output file is a lossless remux (`ffmpeg -c copy`) — no re-encoding, no quality loss. All original streams are preserved: video, all audio tracks, and all subtitle tracks. After splitting, `QueueLibraryScan()` triggers a background library refresh so the new files appear in Jellyfin automatically.

The source movie file is **not modified or deleted**.

---

## API reference

Both endpoints require admin authentication (`RequiresElevation` policy — same level as the Jellyfin dashboard).

### `POST /MovieSplitter/SplitItem`

Splits a single movie by its Jellyfin item ID.

**Query parameters**

| Parameter | Type   | Description                                |
| --------- | ------ | ------------------------------------------ |
| `itemId`  | `Guid` | The Jellyfin item ID of the movie to split |

**Success response**

```json
{ "episodesCreated": 4, "message": null }
```

**Error responses**

```json
{ "episodesCreated": 0, "message": "No subtitles found for this movie." }
```

**Example**

```bash
curl -X POST \
  "http://jellyfin.local:8096/MovieSplitter/SplitItem?itemId=abc123" \
  -H "Authorization: MediaBrowser Token=\"your-api-token\""
```

---

### `GET /MovieSplitter/TestOllama`

Probes an Ollama server to verify connectivity. Used by the Settings page "Test connection" button.

**Query parameters**

| Parameter   | Type     | Description                            |
| ----------- | -------- | -------------------------------------- |
| `ollamaUrl` | `string` | Base URL of the Ollama server to probe |

**Responses**

```json
{ "ok": true,  "error": null }
{ "ok": false, "error": "Connection refused (localhost:11434)" }
```

---

## Project structure

```
MovieSplitter/
├── Plugin.cs                          IPlugin + IHasWebPages
├── PluginConfiguration.cs             Serialised settings (stored as XML by Jellyfin)
├── ServiceRegistration.cs             DI registrations for the Jellyfin host
│
├── Api/
│   └── MovieSplitterController.cs    REST endpoints (/MovieSplitter/*)
│
├── Configuration/
│   ├── configPage.html               Dashboard settings UI (embedded resource)
│   └── detailPagePlugin.js           Client-side detail page + context menu injection
│
├── Detection/
│   ├── IBoundaryDetector.cs          Adapter interface
│   ├── DetectorMode.cs               Heuristic / Ollama / Composite enum
│   ├── CueWordMatcher.cs             Regex cue-word scanner
│   ├── HeuristicBoundaryDetector.cs  Silence gap + cue-word implementation
│   ├── CompositeBoundaryDetector.cs  Multi-detector result merger (15 s collapse window)
│   ├── BoundaryDetectorFactory.cs    Selects correct implementation from config
│   └── Ollama/
│       ├── OllamaClient.cs           HTTP client for Ollama /api/generate
│       └── OllamaBoundaryDetector.cs LLM-based implementation with automatic fallback
│
├── Splitting/
│   └── FfmpegSplitter.cs             Lossless remux via ffmpeg -c copy -map 0
│
├── Subtitle/
│   ├── SubtitleCue.cs                Record: Start, End, Text
│   ├── SrtParser.cs                  SRT file parser
│   └── SubtitleLoader.cs             Sidecar discovery + embedded track extraction
│
└── Tasks/
    └── SplitMovieTask.cs             IScheduledTask orchestrator (full library scan)
```

---

## Troubleshooting

**Plugin does not appear in Dashboard → My Plugins**

Confirm the DLL is in a correctly named subfolder (`MovieSplitter_1.0.0.0/`). Check the Jellyfin log for assembly load errors:

```bash
journalctl -u jellyfin -n 100 | grep -i "moviesplitter\|plugin"
```

Ensure the DLL targets `net8.0` and your Jellyfin server is version 10.9 or later.

---

**"No subtitles found" in the logs**

The plugin requires subtitles. Check that either:

- An external `.srt` or `.ass` file exists alongside the movie — e.g. `Movie.eng.srt` or `Movie.srt`
- The movie container (`.mkv`) has an embedded subtitle track visible in Jellyfin's media info

Also confirm the **Subtitle language** setting matches your sidecar file's language code.

---

**No episode boundaries detected**

- Lower the **Silence gap threshold** (e.g. from 30 s to 15 s)
- Add episode-specific cue word patterns for your content
- Switch to **Ollama** or **Composite** mode for ambiguous content
- Enable `Debug` logging in Jellyfin to see each candidate boundary: `Dashboard → Logs`

---

**Ollama connection fails**

Confirm Ollama is running and accessible from the Jellyfin server:

```bash
curl http://localhost:11434/api/tags
```

If Jellyfin runs in Docker, `localhost` refers to the container — use the host's IP address or a Docker network alias instead of `localhost`. Use the **Test connection** button in plugin settings to diagnose from within the server process.

---

**Split files do not appear in the library**

Wait a minute for the background library scan to complete, then check `Dashboard → Libraries → Scan All Libraries` to trigger a manual refresh. Confirm the output subfolder is inside a path that Jellyfin is configured to monitor.

---

**ffmpeg not found**

Confirm ffmpeg is on the PATH accessible to the Jellyfin service user:

```bash
sudo -u jellyfin which ffmpeg
```

Jellyfin's bundled ffmpeg is used automatically if available. If not, create a symlink:

```bash
ln -s /usr/lib/jellyfin-ffmpeg/ffmpeg /usr/local/bin/ffmpeg
```

---

## License

MIT
