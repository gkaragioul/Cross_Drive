Place offline prerequisite installers in this folder for GKMacOpener builds.

Required:
- winfsp.msi (or WinFsp.msi)

When present, first-run preflight installs WinFsp from this local MSI before
falling back to winget.

Licensing note:
- Ship the unmodified WinFsp MSI only.
- Do not package extracted WinFsp SDK/runtime folders such as winfsp-extract/.
- GKMacOpener uses WinFsp through the FLOSS exception path and must remain
  distributed under an open-source license unless a commercial WinFsp license
  is obtained.
