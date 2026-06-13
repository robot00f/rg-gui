# uninstall-rgrep-context-menu.ps1

& reg.exe delete "HKCU\Software\Classes\Directory\shell\RGrepThisFolder" /f 2>$null | Out-Null
& reg.exe delete "HKCU\Software\Classes\Directory\Background\shell\RGrepThisFolder" /f 2>$null | Out-Null
& reg.exe delete "HKCU\Software\Classes\*\shell\RGrepThisFolder" /f 2>$null | Out-Null

Write-Host "Removed Explorer context menu option: RGrep this folder"