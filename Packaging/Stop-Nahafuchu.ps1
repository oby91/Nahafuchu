$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$builtBinaryRoot = Join-Path $projectRoot 'bin'
$installerRoot = Join-Path $projectRoot 'dist'
$installedRoot = Join-Path $env:LOCALAPPDATA 'Programs\Nahafuchu'
$stopped = [System.Collections.Generic.List[string]]::new()
$couldNotStop = [System.Collections.Generic.List[string]]::new()

function Test-PathUnderRoot {
    param(
        [Parameter(Mandatory)] [string] $Candidate,
        [Parameter(Mandatory)] [string] $Root
    )

    $candidatePath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\') + '\'
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return $candidatePath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)
}

$processes = Get-CimInstance Win32_Process -Filter "Name = 'Nahafuchu.exe' OR Name = 'LayoutFix.exe' OR Name = 'Nahafuchu-Setup.exe'"
foreach ($process in $processes) {
    $executablePath = $process.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
        continue
    }

    $belongsToNahafuchu = $false
    switch -CaseSensitive ($process.Name.ToLowerInvariant()) {
        'nahafuchu.exe' {
            $belongsToNahafuchu = (Test-PathUnderRoot $executablePath $builtBinaryRoot) `
                -or (Test-PathUnderRoot $executablePath $installerRoot) `
                -or (Test-PathUnderRoot $executablePath $installedRoot)
            if (-not $belongsToNahafuchu) {
                try {
                    $belongsToNahafuchu = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductName -eq 'Nahafuchu'
                }
                catch {
                    $belongsToNahafuchu = $false
                }
            }
        }
        'layoutfix.exe' {
            $belongsToNahafuchu = Test-PathUnderRoot $executablePath $builtBinaryRoot
        }
        'nahafuchu-setup.exe' {
            $belongsToNahafuchu = Test-PathUnderRoot $executablePath $installerRoot
        }
    }

    if (-not $belongsToNahafuchu -or $process.ProcessId -eq $PID) {
        continue
    }

    try {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        $stopped.Add("$($process.Name) (PID $($process.ProcessId))")
    }
    catch {
        $couldNotStop.Add("$($process.Name) (PID $($process.ProcessId)): $($_.Exception.Message)")
    }
}

if ($stopped.Count -gt 0) {
    Write-Output 'Stopped Nahafuchu processes:'
    $stopped | ForEach-Object { Write-Output "  $_" }
}
else {
    Write-Output 'No Nahafuchu processes were running.'
}

if ($couldNotStop.Count -gt 0) {
    Write-Warning 'Some matching processes require elevated PowerShell. Run PowerShell as administrator and retry:'
    $couldNotStop | ForEach-Object { Write-Warning "  $_" }
}

