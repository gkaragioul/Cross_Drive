!macro preInit
  SetRegView 64
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\GKMacOpener"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "GKMacOpener"
  SetRegView 32
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\GKMacOpener"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "GKMacOpener"
!macroend

!macro customInit
  ; Kill any running GKMacOpener-related processes so the installer can
  ; overwrite their EXE/DLL files without sharing violations. The native
  ; broker / service / user-session helper are spawned by Electron via
  ; child_process.spawn and survive the parent's app.quit(); without this
  ; cleanup the upgrade installer fails partway through file replacement.
  DetailPrint "Stopping any running GKMacOpener components..."
  nsExec::Exec 'taskkill /F /IM GKMacOpener.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.UserSessionHelper.exe /T'
  Sleep 1500
!macroend

!macro customUnInit
  ; Same cleanup for the uninstall path.
  nsExec::Exec 'taskkill /F /IM GKMacOpener.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.UserSessionHelper.exe /T'
  Sleep 1500
!macroend

!macro customInstall
  ; Install WinFsp from bundled MSI. /i is install-or-update, so this is a
  ; no-op when the same WinFsp is already present (msiexec returns 1638 in
  ; that case which we don't treat as fatal).
  DetailPrint "Installing WinFsp runtime..."
  ExecWait 'msiexec /i "$INSTDIR\resources\prereqs\winfsp.msi" /quiet /norestart' $0
  DetailPrint "WinFsp install exit code: $0"

  ; Wait a moment for the WinFsp service to register
  Sleep 2000
!macroend
