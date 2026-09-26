# Retired Native Bridge Roadmap

This roadmap is kept only as historical context. The customer mount path is now
the bundled native Windows runtime:

- `CrossDrive.NativeService`
- `CrossDrive.NativeBroker`
- `CrossDrive.RawDiskEngine`
- `CrossDrive.UserSessionHelper`
- WinFsp installed from the bundled `prereqs/winfsp.msi`

Do not add customer dependencies on external bridge binaries, package managers,
external downloads, or separate prerequisite setup. New APFS/HFS/HFS+ work
should happen in the native runtime and be covered by `npm run fs:test`,
`npm run installer:smoke`, and real-media validation.
