# Build the framework on Windows: the PowerShell equivalent of build.sh, beside it.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 -OniGame "C:\...\OxygenNotIncluded"
#
# Needs the .NET SDK (winget install Microsoft.DotNet.SDK.8) and the game's install folder
# (ONI_GAME or -OniGame). The SDK simulation library is bundled from oni-sim-replacement cloned
# beside this repository and built first (its sim\build.ps1); ONI_SIM_REPO overrides the path.
# Git is optional: without it the version reads MAJOR.MINOR.0+unknown, exactly as build.sh does.
[CmdletBinding()]
param(
  # The Oxygen Not Included install folder, the one that contains OxygenNotIncluded_Data.
  # Overrides the ONI_GAME environment variable.
  [string]$OniGame
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# ---------------------------------------------------------------- helpers (Windows PowerShell 5.1)

function Fail([string]$Message) {
  [Console]::Error.WriteLine("build.ps1: $Message")
  exit 1
}

# Run a native program, passing its stdout through and its stderr to stderr as plain text, and
# stop on a non-zero exit code. ErrorActionPreference is relaxed for the call itself because
# Windows PowerShell 5.1 turns a native program's stderr lines into errors when output is
# redirected, which would stop the build at the first compiler warning.
function Invoke-Native([string]$What, [string]$Exe, [string[]]$Arguments) {
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    & $Exe @Arguments 2>&1 | ForEach-Object {
      if ($_ -is [System.Management.Automation.ErrorRecord]) { [Console]::Error.WriteLine($_.ToString()) }
      else { $_ }
    }
    $code = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $saved
  }
  if ($code -ne 0) { Fail "$What failed (exit code $code)" }
}

# git output, or $null when git is missing or the command fails (not a checkout, a shallow
# clone, ...). The version stamp degrades to literals rather than stopping the build.
function Get-GitOutput([string[]]$GitArgs) {
  if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { return $null }
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $out = & git @GitArgs 2>$null
    $code = $LASTEXITCODE
  } catch {
    $code = 1
  } finally {
    $ErrorActionPreference = $saved
  }
  if ($code -ne 0) { return $null }
  return ((@($out) | ForEach-Object { "$_" }) -join "`n").Trim()
}

# MAJOR.MINOR.REV+SHA[*]: every non-comment line of the VERSION file with all whitespace removed,
# the commit count and short commit of HEAD, and '*' if the working tree has any change.
function Get-VersionParts([string]$VersionFile) {
  $base = ((Get-Content -LiteralPath $VersionFile | Where-Object { $_ -notmatch '^#' }) -join '') -replace '\s', ''
  $rev = Get-GitOutput @('rev-list', '--count', 'HEAD')
  if (-not $rev) { $rev = '0' }
  $sha = Get-GitOutput @('rev-parse', '--short=7', 'HEAD')
  if (-not $sha) { $sha = 'unknown' }
  $dirty = ''
  if (Get-GitOutput @('status', '--porcelain')) { $dirty = '*' }
  return @{ Base = $base; Rev = $rev; Sha = $sha; Dirty = $dirty }
}

# Write text exactly: UTF-8 without a byte order mark, and only the line endings in $Text.
function Write-Exact([string]$Path, [string]$Text) {
  $full = Join-Path (Get-Location).ProviderPath $Path
  [System.IO.File]::WriteAllText($full, $Text, (New-Object System.Text.UTF8Encoding $false))
}
# The game folder from -OniGame or ONI_GAME, checked to be the one holding OxygenNotIncluded_Data.
function Get-OniGame([string]$Given) {
  $game = $Given
  if (-not $game) { $game = $env:ONI_GAME }
  if (-not $game) {
    Fail ("ONI_GAME is not set. Pass -OniGame or set `$env:ONI_GAME to your Oxygen Not Included " +
          "install folder, the one that contains OxygenNotIncluded_Data (for Steam usually " +
          "C:\Program Files (x86)\Steam\steamapps\common\OxygenNotIncluded).")
  }
  $game = $game.TrimEnd('\', '/')
  if (-not (Test-Path -LiteralPath (Join-Path $game 'OxygenNotIncluded_Data') -PathType Container)) {
    Fail ("'$game' has no OxygenNotIncluded_Data folder. ONI_GAME must be the Oxygen Not Included " +
          "install folder, the one that contains OxygenNotIncluded_Data.")
  }
  return $game
}

# The .NET SDK, from PATH or %USERPROFILE%\.dotnet as build.sh allows ~/.dotnet.
function Find-DotnetSdk {
  $env:PATH = (Join-Path $HOME '.dotnet') + ';' + $env:PATH
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  $d = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $d) { Fail "dotnet was not found. Install the .NET SDK:  winget install Microsoft.DotNet.SDK.8  and open a new PowerShell window." }
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $sdks = $null
  try { $sdks = & $d.Source --list-sdks 2>$null } finally { $ErrorActionPreference = $saved }
  if (-not $sdks) { Fail "$($d.Source) has no SDK installed (only a runtime). Install the .NET SDK:  winget install Microsoft.DotNet.SDK.8" }
  return $d.Source
}

# Lines of *.cs files that call Debug.LogError, skipping comment lines, as grep -n prints them.
function Find-LogErrorCalls([System.IO.FileInfo[]]$Files) {
  if (-not $Files) { return @() }
  return @($Files | Select-String -Pattern 'Debug\.LogError' -CaseSensitive |
           Where-Object { $_.Line -notmatch '^\s*(//|\*|/\*)' } |
           ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line)" })
}

# ---------------------------------------------------------------- build

$savedPath = $env:PATH
$savedGame = $env:ONI_GAME
$savedTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
Push-Location -LiteralPath $PSScriptRoot
try {
  # The game's managed assemblies are referenced from the install (Directory.Build.props reads
  # ONI_GAME), so it is checked here rather than surfacing as missing Unity types.
  $env:ONI_GAME = Get-OniGame $OniGame
  $dotnet = Find-DotnetSdk

  # THE VERSION STAMP, built the same way as the SimDLL's.
  $v = Get-VersionParts 'VERSION'
  $version = "$($v.Base).$($v.Rev)+$($v.Sha)$($v.Dirty)"
  Write-Output "version: $version"

  # Generated, not tracked (.gitignore). Written every build so it can never be stale; the csproj
  # refuses to compile without it. Same bytes as build.sh writes (LF, tabs) apart from line 1.
  $cs = @(
    '// GENERATED BY build.ps1 -- DO NOT EDIT, DO NOT COMMIT. Rewritten on every build from the',
    '// VERSION file and git. See OniFramework/BuildStamp.cs for what reads it.',
    'namespace OniFramework',
    '{',
    "`tinternal static class GeneratedVersion",
    "`t{",
    "`t`tinternal const string Value = `"$version`";",
    "`t}",
    '}'
  )
  Write-Exact 'OniFramework\Version.generated.cs' (($cs -join "`n") + "`n")

  # GUARD: THE FRAMEWORK MAY NOT CALL Debug.LogError (see build.sh for why: the game treats a
  # logged error as a crash and disables the mods on the stack).
  $bad = @(Find-LogErrorCalls @(Get-ChildItem -Path 'OniFramework' -Filter '*.cs' -File))
  if ($bad.Count -gt 0) {
    [Console]::Error.WriteLine('build.ps1: FAILED -- OniFramework may not call Debug.LogError:')
    $bad | ForEach-Object { [Console]::Error.WriteLine($_) }
    [Console]::Error.WriteLine('build.ps1: use OniFramework.FrameworkLog.Error (warning level, ERROR in the text), or')
    [Console]::Error.WriteLine('build.ps1: throw if it really is a crash. See OniFramework/FrameworkLog.cs.')
    exit 1
  }

  Invoke-Native 'dotnet build' $dotnet @('build', 'OniFramework/OniFramework.csproj', '-c', 'Release', '--nologo', '-v', 'minimal')
  Write-Output "built OniFramework/bin/Release/OniFramework.dll  ($version)"

  # THE SDK SIMDLL, BUNDLED FOR SimDllDelivery, into native\ of the mod folder: the library, its
  # SHA-256, the game builds it supports and its version. Rebuilt every time, so a framework
  # built without a library never carries a stale one. The text files are written with LF line
  # endings whatever the checkout uses, so native\ matches a build.sh build byte for byte.
  $out = 'OniFramework\bin\Release'
  $native = Join-Path $out 'native'
  if (Test-Path -LiteralPath $native) { Remove-Item -LiteralPath $native -Recurse -Force }
  $simRepo = $env:ONI_SIM_REPO
  if (-not $simRepo -and (Test-Path -LiteralPath '..\oni-sim-replacement\sim' -PathType Container)) {
    $simRepo = '..\oni-sim-replacement'
  }
  if ($simRepo -and (Test-Path -LiteralPath (Join-Path $simRepo 'sim\build\SimDLL.dll') -PathType Leaf)) {
    $builds = Join-Path $simRepo 'installer\supported-builds.txt'
    if (-not (Test-Path -LiteralPath $builds -PathType Leaf)) { Fail "$builds is missing" }
    New-Item -ItemType Directory -Force -Path $native | Out-Null
    Copy-Item -LiteralPath (Join-Path $simRepo 'sim\build\SimDLL.dll') -Destination (Join-Path $native 'SimDLL.dll')
    $list = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $builds).ProviderPath) -replace "`r`n", "`n"
    Write-Exact (Join-Path $native 'supported-builds.txt') $list
    $first = @(Get-Content -LiteralPath (Join-Path $simRepo 'sim\VERSION') | Where-Object { $_ -match '^[^#\s]' } | Select-Object -First 1)
    $simVersion = ''
    if ($first.Count -gt 0) { $simVersion = $first[0] + "`n" }
    Write-Exact (Join-Path $native 'VERSION') $simVersion
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $native 'SimDLL.dll')).Hash.ToLower()
    Write-Exact (Join-Path $native 'SHA256SUMS') "$hash  SimDLL.dll`n"
    Write-Output "bundled OniFramework/bin/Release/native/SimDLL.dll  ($($simVersion.Trim()), $($hash.Substring(0, 8)))"
  } else {
    [Console]::Error.WriteLine('build.ps1: no sim/build/SimDLL.dll found (set ONI_SIM_REPO), so the mod carries no SimDLL')
  }
} finally {
  Pop-Location
  $env:PATH = $savedPath
  $env:ONI_GAME = $savedGame
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = $savedTelemetry
}
