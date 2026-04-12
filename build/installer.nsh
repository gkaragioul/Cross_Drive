!macro preInit
  SetRegView 64
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\MacMount"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "MacMount"
  SetRegView 32
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" InstallLocation "$LocalAppData\Programs\MacMount"
  WriteRegExpandStr HKLM "${INSTALL_REGISTRY_KEY}" StartMenuLink "MacMount"
!macroend

!macro customInstall
  ; Install WinFsp from bundled MSI
  DetailPrint "Installing WinFsp runtime..."
  ExecWait 'msiexec /i "$INSTDIR\resources\prereqs\winfsp.msi" /quiet /norestart' $0
  DetailPrint "WinFsp install exit code: $0"

  ; Wait a moment for the WinFsp service to register
  Sleep 2000
!macroend
