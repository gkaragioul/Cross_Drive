' CrossDrive silent production launcher.
' Runs "npm run start:prod" with no visible terminal window.
' Double-click this file to start CrossDrive without any console windows appearing.

Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)

' Run hidden (window style 0 = invisible), non-blocking (bWaitOnReturn = False)
WshShell.Run "cmd /c cd /d """ & scriptDir & """ && npm run start:prod", 0, False
