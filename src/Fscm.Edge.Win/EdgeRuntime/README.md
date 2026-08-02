This directory is populated by `scripts/build-edge-backend.ps1`.

Expected packaged files:

- `fscm-edge.exe`
- `edge.config.yaml`
- `edge-runtime-manifest.json`

The Windows app creates a default `edge.config.yaml` at runtime when it is missing, but a packaged build should include all files above.

The installer places the service executable in `%ProgramData%\FSCM Edge\Service` and mutable configuration, SQLite data, templates, and logs in `%ProgramData%\FSCM Edge\EdgeRuntime`. Keeping the service binary separate prevents standard users from replacing an executable that runs under the Windows service account.
