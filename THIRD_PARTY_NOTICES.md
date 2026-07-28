# Third-party notices

Stage 6 optionally packages the local `music-analyzer` with:

- librosa — ISC License;
- NumPy — BSD-3-Clause;
- SoundFile — BSD-3-Clause;
- PyInstaller — GPL-2.0-or-later with its bootloader exception.

Their exact transitive dependency versions are resolved from
`tools/music-analyzer/requirements.txt` during the isolated build. Distribution
must retain the license files emitted by the packaged dependency set.

No third-party LUT is currently bundled. Stage 6 color presets use explicit
FFmpeg filters; future `.cube` assets must be whitelisted and documented here
before distribution.
