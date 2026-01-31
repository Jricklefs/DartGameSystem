$WshShell = New-Object -ComObject WScript.Shell
$DesktopPath = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = "$DesktopPath\DartsMob.lnk"

# Get the script's directory (where DartGameSystem is)
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDir) { $ScriptDir = Get-Location }

$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "$ScriptDir\START_DARTSMOB.bat"
$Shortcut.WorkingDirectory = $ScriptDir
$Shortcut.Description = "Launch DartsMob Dart Game System"
$Shortcut.WindowStyle = 1

# Use a dart/target emoji icon from Windows (or custom ico if exists)
$IconPath = "$ScriptDir\DartsMob.ico"
if (Test-Path $IconPath) {
    $Shortcut.IconLocation = $IconPath
} else {
    # Fallback to a Windows system icon (target/bullseye style)
    $Shortcut.IconLocation = "%SystemRoot%\System32\shell32.dll,176"
}

$Shortcut.Save()

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  DartsMob shortcut created on Desktop!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "If you have a DartsMob.ico file, place it in:"
Write-Host "  $ScriptDir\DartsMob.ico"
Write-Host ""
Write-Host "Then run this script again to update the icon."
Write-Host ""
