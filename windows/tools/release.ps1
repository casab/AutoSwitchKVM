<#
.SYNOPSIS
  Build a Velopack release of AutoSwitch KVM for Windows (installer + update packages).

.DESCRIPTION
  Publishes the app self-contained (.NET + Windows App SDK bundled, nothing to install first) and
  packs it with Velopack (vpk). Output goes to windows/releases/<arch>/:
    AutoSwitchKVM.App-win-Setup.exe     the installer (per-user, no admin)
    AutoSwitchKVM.App-<ver>-full.nupkg  the update package
    releases.win.json, RELEASES         the update feed

  The GitHub release workflow (.github/workflows/windows-release.yml) runs this script for a v* tag
  and uploads the result. Run it locally to test an installer:
    pwsh windows/tools/release.ps1 -Version 0.1.0
  Add -Download to fetch the previous GitHub release first, so a delta package is generated.

  Pure ASCII on purpose (PowerShell 5.1 reads BOM-less scripts as Windows-1252).

.NOTES
  packId is "AutoSwitchKVM.App", NOT "AutoSwitchKVM": Velopack installs to %LOCALAPPDATA%\<packId>
  and deletes that folder on uninstall, while the config and logs live in
  %LOCALAPPDATA%\AutoSwitchKVM and must survive an uninstall/reinstall.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [switch] $Download,
    [string] $Token,
    [string] $RepoUrl = 'https://github.com/casab/AutoSwitchKVM'
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer (e.g. 1.2.3 or 1.2.3-beta.1), got '$Version'"
}

$root = Split-Path -Parent $PSScriptRoot          # windows/
$project = Join-Path $root 'src\AutoSwitchKVM.App\AutoSwitchKVM.App.csproj'
$icon = Join-Path $root 'src\AutoSwitchKVM.App\Assets\app.ico'
$publishDir = Join-Path $root "publish\win-$Arch"
$outDir = Join-Path $root "releases\$Arch"
# x64 keeps Velopack's default channel ("win"); other architectures get their own feed, so an
# installed app only ever updates to builds for its own architecture.
$channel = if ($Arch -eq 'x64') { 'win' } else { "win-$Arch" }

Push-Location $root
try {
    function Invoke-Step([string] $what, [scriptblock] $cmd) {
        Write-Host "==> $what" -ForegroundColor Cyan
        & $cmd
        if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
    }

    Invoke-Step 'Restore tools (vpk)' { dotnet tool restore }

    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Invoke-Step "Publish $Version ($Arch, self-contained)" {
        dotnet publish $project -c Release -r "win-$Arch" -p:Platform=$Arch `
            --self-contained true -p:WindowsAppSDKSelfContained=true `
            -p:Version=$Version -o $publishDir
    }

    # Start from an empty output folder: vpk builds deltas against whatever packages it finds there.
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    if ($Download) {
        # The previous full package lets vpk build a delta update (much smaller download). Not
        # fatal: the first release (or a new channel) has nothing to download.
        Write-Host "==> Download previous release (for deltas)" -ForegroundColor Cyan
        $tokenArgs = if ($Token) { @('--token', $Token) } else { @() }
        dotnet vpk download github --repoUrl $RepoUrl --channel $channel -o $outDir @tokenArgs
        if ($LASTEXITCODE -ne 0) { Write-Warning "no previous release downloaded; building a full package only" }
    }

    Invoke-Step "Pack with Velopack (channel $channel)" {
        dotnet vpk pack --packId AutoSwitchKVM.App --packVersion $Version --packDir $publishDir `
            --mainExe AutoSwitchKVM.App.exe --packTitle 'AutoSwitch KVM' --packAuthors 'AutoSwitch KVM' `
            --icon $icon --channel $channel --runtime "win-$Arch" --shortcuts StartMenuRoot -o $outDir
    }

    Write-Host "Done: $outDir" -ForegroundColor Green
    Get-ChildItem $outDir | Format-Table Name, Length -AutoSize
}
finally {
    Pop-Location
}
