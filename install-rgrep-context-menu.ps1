# install-rgrep-context-menu.ps1
# Adds "RGrep this folder" to Windows Explorer context menus.
# rg-gui.exe must be in the same folder as this script.

$ErrorActionPreference = "Stop"

function Wait-BeforeExit {
    param([string]$Message = "Press Enter to close this window...")

    Write-Host ""
    Write-Host $Message

    try {
        [void][System.Console]::ReadLine()
    } catch {
        Start-Sleep -Seconds 10
    }
}

try {
    $scriptDir = if ($PSScriptRoot) {
        $PSScriptRoot
    } else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    Set-Location -LiteralPath $scriptDir

    $rgGuiExe = Join-Path $scriptDir "rg-gui.exe"
    $vbsLauncher = Join-Path $scriptDir "rgrep-context-launcher.vbs"
    $wscriptExe = Join-Path $env:SystemRoot "System32\wscript.exe"

    if (-not (Test-Path -LiteralPath $rgGuiExe)) {
        throw "Could not find rg-gui.exe in: $scriptDir"
    }

    if (-not (Test-Path -LiteralPath $wscriptExe)) {
        throw "Could not find wscript.exe at: $wscriptExe"
    }

    @'
Option Explicit

Dim selectedPath, isFile, scriptDir, rgGuiExe, folder, extension, fso, shell, cmd

If WScript.Arguments.Count < 1 Then
    WScript.Quit 1
End If

selectedPath = WScript.Arguments(0)
isFile = False

If WScript.Arguments.Count >= 2 Then
    If LCase(WScript.Arguments(1)) = "file" Then
        isFile = True
    End If
End If

Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
rgGuiExe = fso.BuildPath(scriptDir, "rg-gui.exe")

If Not fso.FileExists(rgGuiExe) Then
    MsgBox "Could not find rg-gui.exe next to launcher: " & rgGuiExe, vbCritical, "RGrep this folder"
    WScript.Quit 2
End If

If isFile Then
    folder = fso.GetParentFolderName(selectedPath)
    extension = fso.GetExtensionName(selectedPath)
Else
    folder = selectedPath
    extension = ""
End If

Set shell = CreateObject("WScript.Shell")

If isFile And extension <> "" Then
    cmd = """" & rgGuiExe & """ --folder """ & folder & """ --include-files ""*." & extension & """ --text ""."""
Else
    cmd = """" & rgGuiExe & """ --folder """ & folder & """"
End If

' 1 = show rg-gui.exe normally
' False = do not wait
shell.Run cmd, 1, False
'@ | Set-Content -LiteralPath $vbsLauncher -Encoding ASCII

    function Remove-RegistryTree {
        param(
            [Parameter(Mandatory = $true)]
            [string]$SubKey
        )

        $classesRoot = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Classes", $true)

        if ($null -eq $classesRoot) {
            throw "Could not open HKCU\Software\Classes for writing."
        }

        try {
            $classesRoot.DeleteSubKeyTree($SubKey, $false)
        } finally {
            $classesRoot.Close()
        }
    }

    function Add-ContextMenuEntry {
        param(
            [Parameter(Mandatory = $true)]
            [string]$SubKey,

            [Parameter(Mandatory = $true)]
            [string]$Command
        )

        $classesRoot = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Classes", $true)

        if ($null -eq $classesRoot) {
            throw "Could not open HKCU\Software\Classes for writing."
        }

        try {
            $menuKey = $classesRoot.CreateSubKey($SubKey)

            if ($null -eq $menuKey) {
                throw "Could not create registry key: HKCU\Software\Classes\$SubKey"
            }

            $menuKey.SetValue("", "RGrep this folder", [Microsoft.Win32.RegistryValueKind]::String)
            $menuKey.SetValue("MUIVerb", "RGrep this folder", [Microsoft.Win32.RegistryValueKind]::String)
            $menuKey.SetValue("Icon", "`"$rgGuiExe`"", [Microsoft.Win32.RegistryValueKind]::String)

            $commandKey = $menuKey.CreateSubKey("command")

            if ($null -eq $commandKey) {
                throw "Could not create registry key: HKCU\Software\Classes\$SubKey\command"
            }

            $commandKey.SetValue("", $Command, [Microsoft.Win32.RegistryValueKind]::String)

            $commandKey.Close()
            $menuKey.Close()
        } finally {
            $classesRoot.Close()
        }
    }

    Write-Host ""
    Write-Host "Removing old context menu entries if present..."

    Remove-RegistryTree "Directory\shell\RGrepThisFolder"
    Remove-RegistryTree "Directory\Background\shell\RGrepThisFolder"
    Remove-RegistryTree "*\shell\RGrepThisFolder"

    $folderCommand = "`"$wscriptExe`" `"$vbsLauncher`" `"%1`""
    $backgroundCommand = "`"$wscriptExe`" `"$vbsLauncher`" `"%V`""
    $fileCommand = "`"$wscriptExe`" `"$vbsLauncher`" `"%1`" file"

    Write-Host "Adding folder context menu entry..."
    Add-ContextMenuEntry `
        -SubKey "Directory\shell\RGrepThisFolder" `
        -Command $folderCommand

    Write-Host "Adding folder background context menu entry..."
    Add-ContextMenuEntry `
        -SubKey "Directory\Background\shell\RGrepThisFolder" `
        -Command $backgroundCommand

    Write-Host "Adding file context menu entry..."
    Add-ContextMenuEntry `
        -SubKey "*\shell\RGrepThisFolder" `
        -Command $fileCommand

    Write-Host ""
    Write-Host "Installed Explorer context menu option: RGrep this folder"
    Write-Host "rg-gui.exe path: $rgGuiExe"
    Write-Host "Launcher path: $vbsLauncher"
    Write-Host ""
    Write-Host "Done."

    Wait-BeforeExit
}
catch {
    Write-Host ""
    Write-Host "Installation failed." -ForegroundColor Red
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""

    Wait-BeforeExit
    exit 1
}