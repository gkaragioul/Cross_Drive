!macro preInit
  SetRegView 64
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\CrossDrive"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "CrossDrive"
  SetRegView 32
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\CrossDrive"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "CrossDrive"
!macroend

!macro customInit
  ; Kill any running CrossDrive-related processes so the installer can
  ; overwrite their EXE/DLL files without sharing violations. The native
  ; broker / service / user-session helper are spawned by Electron via
  ; child_process.spawn and survive the parent's app.quit(); without this
  ; cleanup the upgrade installer fails partway through file replacement.
  DetailPrint "Stopping any running CrossDrive components..."
  nsExec::Exec 'taskkill /F /IM CrossDrive.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.UserSessionHelper.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.UserSessionHelper.exe /T'
  Sleep 1500
!macroend

!macro customUnInit
  ; Same cleanup for the uninstall path.
  nsExec::Exec 'taskkill /F /IM CrossDrive.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM CrossDrive.UserSessionHelper.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeBroker.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.NativeService.exe /T'
  nsExec::Exec 'taskkill /F /IM MacMount.UserSessionHelper.exe /T'
  Sleep 1500
!macroend

!macro customInstall
  ; Install WinFsp from the bundled MSI only when it is missing. Re-running
  ; the MSI during app upgrades can make the installer appear stuck near the
  ; end while Windows Installer performs a silent repair/reconfigure pass.
  IfFileExists "$PROGRAMFILES32\WinFsp\bin\launchctl-x64.exe" crossdrive_winfsp_installed 0
  IfFileExists "$PROGRAMFILES64\WinFsp\bin\launchctl-x64.exe" crossdrive_winfsp_installed 0
  DetailPrint "Installing WinFsp runtime..."
  ExecWait 'msiexec /i "$INSTDIR\resources\prereqs\winfsp.msi" /quiet /norestart' $0
  DetailPrint "WinFsp install exit code: $0"
  Goto crossdrive_winfsp_done

  crossdrive_winfsp_installed:
  DetailPrint "Skipping bundled WinFsp runtime; already installed."

  crossdrive_winfsp_done:

  ; Wait a moment for the WinFsp service to register
  Sleep 2000

  ; Unsigned staging builds can skip executable resource editing, which means
  ; Windows shortcuts may fall back to Electron's generic icon. Point the
  ; shortcuts directly at the packaged icon resource so the desktop tile stays
  ; branded even before code signing is configured.
  Delete "$DESKTOP\CrossDrive.lnk"
  CreateShortCut "$DESKTOP\CrossDrive.lnk" "$INSTDIR\CrossDrive.exe" "" "$INSTDIR\resources\icon.ico" 0
  Delete "$SMPROGRAMS\CrossDrive.lnk"
  CreateShortCut "$SMPROGRAMS\CrossDrive.lnk" "$INSTDIR\CrossDrive.exe" "" "$INSTDIR\resources\icon.ico" 0
!macroend
