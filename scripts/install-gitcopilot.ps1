param(
    [string]$InstallRoot = "$env:LOCALAPPDATA\gitcopilot",
    [switch]$Aot,
    [string]$Runtime = "win-x64",
    [switch]$ForceAot
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\")).Path
$sourceFile = Join-Path $repoRoot "copilot.cs"

if (-not (Test-Path $sourceFile)) {
    throw "Could not find copilot.cs at $sourceFile"
}

$publishDir = Join-Path $InstallRoot "app"
$binDir = Join-Path $InstallRoot "bin"

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $binDir -Force | Out-Null

Write-Host "Building gitcopilot (Release)..."
dotnet build $sourceFile -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed"
}

$publishSucceeded = $false

if ($Aot) {
    Write-Host "Publishing gitcopilot (Release + AOT, Runtime=$Runtime)..."
    dotnet publish $sourceFile -c Release -r $Runtime --self-contained true -o $publishDir /p:PublishAot=true /p:PublishSingleFile=true /p:InvariantGlobalization=true
    if ($LASTEXITCODE -eq 0) {
        $publishSucceeded = $true
    }
    elseif ($ForceAot) {
        throw "dotnet publish failed in AOT mode and -ForceAot is set"
    }
    else {
        Write-Host "AOT publish failed. Falling back to standard Release publish..."
    }
}

if (-not $publishSucceeded) {
    Write-Host "Publishing gitcopilot (Release)..."
    dotnet publish $sourceFile -c Release -o $publishDir --no-self-contained /p:PublishAot=false /p:PublishSingleFile=false /p:SelfContained=false /p:RuntimeIdentifier=
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed"
    }
}

$publishedExe = Join-Path $publishDir "copilot.exe"
if (-not (Test-Path $publishedExe)) {
    $candidateExe = Get-ChildItem -Path $publishDir -Filter "*.exe" -File | Select-Object -First 1
    if ($null -eq $candidateExe) {
        throw "No executable produced in $publishDir"
    }

    $publishedExe = $candidateExe.FullName
}

# Remove legacy executable shim from older installer versions.
# If left in place, PATHEXT resolution prefers .exe over .cmd and breaks launching.
$legacyExeShim = Join-Path $binDir "gitcopilot.exe"
if (Test-Path $legacyExeShim) {
    Remove-Item $legacyExeShim -Force
}

$cmdShim = Join-Path $binDir "gitcopilot.cmd"
@"
@echo off
setlocal
set "APP_EXE=$publishedExe"
if "%~1"=="" (
    "%APP_EXE%" --repl
) else (
    "%APP_EXE%" %*
)
endlocal
"@ | Set-Content -Path $cmdShim -Encoding ASCII

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathEntries = @()
if (-not [string]::IsNullOrWhiteSpace($userPath)) {
    $pathEntries = $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$exists = $pathEntries | Where-Object { $_.TrimEnd('\\') -ieq $binDir.TrimEnd('\\') }
if (-not $exists) {
    $newPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $binDir } else { "$userPath;$binDir" }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Host "Added to user PATH: $binDir"
}
else {
    Write-Host "PATH already contains: $binDir"
}

# Also update PATH for the current process so gitcopilot is immediately callable in this shell.
$processEntries = ($env:Path -split ';') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$processHasBin = $processEntries | Where-Object { $_.TrimEnd('\\') -ieq $binDir.TrimEnd('\\') }
if (-not $processHasBin) {
    $env:Path = "$env:Path;$binDir"
    Write-Host "Added to current session PATH: $binDir"
}

Write-Host ""
Write-Host "Install complete."
Write-Host "Open a new terminal and run: gitcopilot"
Write-Host 'Optional one-shot mode: gitcopilot --profile cloud "summarize this diff"'

Write-Host ""
Write-Host "Running post-install self-check..."
& $publishedExe --help | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Self-check passed: gitcopilot launcher is executable."
}
else {
    throw "Self-check failed: launcher execution returned exit code $LASTEXITCODE"
}
