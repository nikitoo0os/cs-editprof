# CS2 Demo Playback Fix provenance

- Upstream: https://github.com/unicbm/cs2-demo-playback-fix-260720
- Version: v0.1.1
- Release date: 2026-07-17
- License: Apache-2.0
- Release archive SHA-256: `766fd7af47e74e46a6ed942ba3c666ae2cdf0da2f1afe43cf40b05dfd32f2891`

The bundled executable removes only strictly validated legacy Source 2 entity
message type 138 (`CEntityMessageRemoveAllDecals`) from a copied PBDEMS2 demo.
The upstream tool never overwrites its input.
