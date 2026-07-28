# Known issues

- No HLAE installation or CS2 test demo was available on the development machine, so real E2E rendering is not claimed.
- The official HLAE GUI documents CS2 launching through the CS2 Launcher or Custom Loader. An unattended `HLAE.exe` CLI contract was not confirmed.
- `mirv_streams`, `mirv_cmd`, and `mirv_fov` are documented for AfxHookSource2. `playdemo`, `demo_gototick`, and `spec_player` still require manual verification against the installed CS2 build.
- The current output watcher checks existence, minimum size, and stability. ffprobe metadata validation remains to be added after a real output format is selected.
- Process ownership cleanup is limited to the process tree started by `ProcessSupervisor`; no broad name-based killing is performed.
