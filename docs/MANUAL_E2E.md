# Manual E2E checklist

1. Record Windows, .NET, Steam, CS2, HLAE, GPU/driver, and demo versions.
2. Launch CS2 with HLAE manually using AfxHookSource2, `-insecure`, `+sv_lan 1`, `-console`, and an isolated `USRLOCALCSGO`.
3. Confirm `NetConPortAvailable`, `FFmpeg`, and `FFprobe` are `True` in
   `scripts/verify-environment.ps1`.
4. Run exactly one command:
   `render-agent.exe render --job render-job.json`.
5. Without touching CS2, verify the demo loads, the requested tick is reached,
   the player POV is selected, recording starts and stops, and
   `raw-highlight.mp4` is produced.
6. Confirm `render-result.json` reports success and inspect `logs/netcon.log`
   for load, seek, POV, recording-start, and recording-end evidence.
7. Confirm no HLAE-launched CS2 remains and the NetCon port is free.
8. Run ffprobe independently against `raw-highlight.mp4` and compare duration
   and dimensions with the job.
9. Repeat with unique job IDs and output directories at least ten times without
   rebooting Windows. After the first successful manual acceptance, this can be
   automated with:
   `scripts/run-acceptance.ps1 -JobPath .\render-job.json -Count 10`.

Never connect this HLAE instance to a VAC-protected server.
