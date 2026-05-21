<!-- Release notes for the next version. Edit before running scripts/publish-release.ps1. -->
<!-- The publish script appends "SHA256: <hex>" to the bottom -- do not add it manually. -->

## Summary

Zero-dependency customer runtime release.

## Notable changes

- **Native runtime by default:** CrossDrive now starts in the bundled native mount path instead of requiring WSL2.
- **No WSL setup blocker:** missing WSL2/Ubuntu is treated as an optional advanced-mode warning and no longer disables Mount buttons.
- **Explicit WSL mode only:** the WSL kernel path is used only when `CROSSDRIVE_MOUNT_MODE=wsl_kernel` is set.
- **Release guardrails:** self-test now verifies the default native runtime, optional WSL handling, and UI mount gating.

## Where to download

Permanent installer link: https://github.com/gkaragioul/Cross_Drive/releases/latest/download/CrossDriveSetup.exe
