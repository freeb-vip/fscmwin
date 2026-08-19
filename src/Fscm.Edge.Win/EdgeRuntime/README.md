This directory is populated by `scripts/build-edge-backend.ps1`.

Expected packaged files:

- `fscm-edge.exe`
- `edge.config.yaml`
- `edge-runtime-manifest.json`

The Windows app creates a default `edge.config.yaml` at runtime when it is missing, but a packaged build should include all files above.

The installer places the service executable in the selected application directory under `EdgeRuntime` (by default, `%ProgramFiles%\FSCM Edge\EdgeRuntime`) and mutable configuration, SQLite data, templates, and logs in `%ProgramData%\FSCM Edge\EdgeRuntime`. The application directory protects the service executable from standard-user modification while keeping operator-managed state writable in ProgramData.
