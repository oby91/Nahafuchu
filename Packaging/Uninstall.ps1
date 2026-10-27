$ErrorActionPreference = 'SilentlyContinue'

$installRoot = $PSScriptRoot
$startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Nahafuchu'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Nahafuchu'
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$executable = Join-Path $installRoot 'Nahafuchu.exe'

Get-Process -Name Nahafuchu | Where-Object { $_.Path -eq $executable } | Stop-Process -Force
Remove-Item -LiteralPath $startMenuRoot -Recurse -Force
Remove-Item -LiteralPath $uninstallKey -Recurse -Force
Remove-ItemProperty -LiteralPath $startupKey -Name 'Nahafuchu' -Force

$cleanupPath = Join-Path $env:TEMP ("Nahafuchu-Uninstall-{0}.cmd" -f [guid]::NewGuid().ToString('N'))
$cleanupScript = @"
@echo off
timeout /t 2 /nobreak >nul
rmdir /s /q "$installRoot"
del "%~f0"
"@
Set-Content -LiteralPath $cleanupPath -Value $cleanupScript -Encoding Ascii
Start-Process -FilePath $env:ComSpec -ArgumentList "/c `"$cleanupPath`"" -WindowStyle Hidden

