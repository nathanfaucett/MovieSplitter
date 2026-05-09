# Movie Splitter — Jellyfin Plugin

Automatically segments long-form video files into **logically coherent episodes**.

This plugin analyzes subtitle timing and text patterns to split a single media file into multiple parts that _make narrative sense_, even when the source file was not originally structured as episodes.

It is designed for:

- Miniseries packaged as a single movie file
- TV broadcasts merged into one recording
- Fan edits or archival releases
- Any long-form content with implicit episode structure

---

## How it works

Rather than blindly splitting at fixed intervals, Movie Splitter attempts to infer **natural episode boundaries** based on narrative and subtitle structure.

The detection pipeline:

1. **Subtitle extraction**
    - Loads external `.srt` / `.ass` files or extracts embedded subtitle tracks via ffmpeg

2. **Boundary inference**
    - Detects likely episode transitions using:
        - Long gaps in dialogue (scene / episode breaks)
        - Narrative cue phrases (“Previously on…”, “Next time…”, chapter markers)
        - Structural patterns in subtitle timing and density
        - (Optional) LLM-based semantic segmentation via Ollama

3. **Coherence-based segmentation**
    - Instead of fixed thresholds, boundaries are selected where content shift is most likely
    - Nearby candidate breaks are merged to avoid over-splitting

4. **Lossless splitting**
    - Uses ffmpeg remuxing (`-c copy`) to avoid re-encoding or quality loss

5. **Library refresh**
    - Newly created episode files are automatically scanned into Jellyfin

---

## Core idea

The plugin does **not assume episodes already exist**.

Instead, it tries to answer:

> “Where would this story naturally break into episodes if it had originally been structured that way?”

This makes it suitable for messy or archival media where episode structure is implied, not explicit.
