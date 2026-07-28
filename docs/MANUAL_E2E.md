# Manual E2E checklist

1. Record Windows, .NET, Steam, CS2, HLAE, GPU/driver, and demo versions.
2. Launch CS2 with HLAE manually using AfxHookSource2, `-insecure`, `+sv_lan 1`, `-console`, and an isolated `USRLOCALCSGO`.
3. In the HLAE console, verify each generated command and use built-in command help.
4. Verify the demo loads, the requested tick is reached, the player POV is selected, recording starts and stops, and an MP4 is produced.
5. Put the exact, verified unattended launcher arguments in `appsettings.local.json`.
6. Only then set `AutomationVerified` to `true`.
7. Run ten consecutive jobs and record success rate, tick drift, duration drift, and cleanup behavior.

Never connect this HLAE instance to a VAC-protected server.
