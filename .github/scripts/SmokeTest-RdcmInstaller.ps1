# Smoke-test a freshly built per-user RDCM MSI on an ephemeral Windows CI runner.
# This is NOT a replacement for the manual real-RDP and upgrade/rollback release checklist.
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$msiName = [IO.Path]::GetFileName($installer)
if ($msiName -ne 'RDCM-1.0.0-win-x64.msi') {
    throw "Unexpected MSI filename: $msiName"
}

# Verify the actual MSI ProductVersion from its installer database, not its filename.
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$db = $windowsInstaller.OpenDatabase($installer, 0)
$view = $db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
$view.Execute()
$row = $view.Fetch()
$version = if ($null -ne $row) { [string] $row.StringData(1) } else { '' }
$view.Close()
if ($version -ne '1.0.0') { throw "MSI ProductVersion mismatch: expected 1.0.0, found '$version'" }
Write-Host "Installer ProductVersion: $version"

# Avoid accidentally modifying another installation on a shared/self-hosted machine.
$exe = Join-Path $env:LOCALAPPDATA 'Programs\RDCM\RDCM.exe'
if (Test-Path -LiteralPath $exe) { throw "RDCM already installed at $exe; refusing destructive test" }

$userdata = Join-Path $env:LOCALAPPDATA 'RDCM'
New-Item -ItemType Directory -Path $userdata -Force | Out-Null
$sentinel = Join-Path $userdata 'ci-install-preservation-check.txt'
if (Test-Path -LiteralPath $sentinel) { throw "Existing test sentinel at $sentinel" }
$marker = 'RDCM test data: installation must not delete me'
Set-Content -LiteralPath $sentinel -Value $marker -Encoding UTF8

$installSucceeded = $false
function Invoke-Msi {
    param([string] $Mode, [string] $LogName)
    $log = Join-Path $env:RUNNER_TEMP $LogName
    $arguments = "/$Mode `"$installer`" /qn /norestart /L*v `"$log`""
    $process = Start-Process -FilePath msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        if (Test-Path $log) { Get-Content $log -Tail 70 | Write-Host }
        throw "msiexec /$Mode returned $($process.ExitCode) (see $log)"
    }
}

try {
    Write-Host 'Running a silent per-user install on the ephemeral CI runner.'
    Invoke-Msi -Mode 'i' -LogName 'rdcm-install.log'
    $installSucceeded = $true
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "MSI install reported success, but RDCM.exe was not installed"
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($fileVersion -notlike '1.0.0*') {
        throw "Installed RDCM.exe version mismatch: $fileVersion"
    }
    Write-Host "Installed RDCM.exe: $fileVersion"
}
finally {
    if ($installSucceeded) {
        Write-Host 'Uninstalling RDCM without touching per-user data.'
        Invoke-Msi -Mode 'x' -LogName 'rdcm-uninstall.log'
    }
}

if (Test-Path -LiteralPath $exe) {
    throw 'RDCM.exe remains after uninstall'
}
if (-not (Test-Path -LiteralPath $sentinel)) {
    throw 'Uninstall deleted user data'
}
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne $marker) {
    throw 'Uninstall modified user data'
}
Remove-Item -LiteralPath $sentinel
Write-Host 'PASS: MSI version, install, application file, uninstall, and user-data preservation checks.'
