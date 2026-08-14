# Third-party notices

Stage 6 optionally packages the local `music-analyzer` with:

- librosa — ISC License;
- NumPy — BSD-3-Clause;
- SciPy — BSD-3-Clause;
- Numba — BSD-2-Clause;
- llvmlite — BSD-2-Clause;
- scikit-learn — BSD-3-Clause;
- SoundFile — BSD-3-Clause;
- PyInstaller — GPL-2.0-or-later with its bootloader exception.

Their exact transitive dependency versions are resolved from
`tools/music-analyzer/requirements.txt` during the isolated build. Distribution
must retain the license files emitted by the packaged dependency set.

No third-party LUT is currently bundled. Stage 6 accepts `.cube` assets only
from the configured local whitelist under `assets/luts`; every added asset must
be reviewed and documented here before distribution.

The Steam match-code importer uses:

- SharpCompress 0.50.1 — MIT License;
- DoctorMcKay/steam-user 5.3.0 — MIT License;
- DoctorMcKay/node-globaloffensive 3.3.0 — MIT License;
- DoctorMcKay/steam-session 1.9.4 — MIT License;
- soldair/node-qrcode 1.5.4 — MIT License;
- gtanner/qrcode-terminal 0.12.0 — MIT License;
- akiver/boiler-writter 1.7.0 — MIT License (installed by
  `scripts/install-boiler-writter.ps1`, not bundled in source control).
