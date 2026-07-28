# Known issues

- No HLAE installation or CS2 test demo was available on the development machine, so real E2E rendering is not claimed.
- The unattended HLAE custom-loader CLI is confirmed from upstream source and used with `-insecure` plus isolated `USRLOCALCSGO`.
- `mirv_streams`, `mirv_cmd`, and `mirv_fov` are documented for AfxHookSource2. `playdemo`, `demo_gototick`, and `spec_player` still require manual verification against the installed CS2 build.
- Current CS2 clients can reject legacy entity message type 138 in otherwise
  valid demos. The agent applies the narrow, fail-closed
  `cs2-demo-playback-fix` v0.1.1 rewrite to its isolated copy before playback.
  This does not guarantee compatibility with every historical demo format.
- The current output watcher checks existence, minimum size, and stability. ffprobe metadata validation remains to be added after a real output format is selected.
- Process ownership cleanup is limited to the process tree started by `ProcessSupervisor`; no broad name-based killing is performed.
