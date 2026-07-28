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
