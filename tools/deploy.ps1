<#
.SYNOPSIS
    Narrow-scope deploy script for the STS2Portrait mod: copy a fixed allow-list into the
    game's own mods folder for this mod only, then read the copies back and compare hashes.

.DESCRIPTION
    Card A08 of docs/Plan. A08 builds and proves the script in DRY-RUN only; the first real
    copy is card A09. To make that split impossible to get wrong, the script DEFAULTS TO
    DRY-RUN and requires -Apply to write anything.

    What it refuses to do (each one is checked, not just documented):
      * it never deploys unless the build report is schema 2.0 AND green AND every input
        hash the report recorded still matches the files on disk AND the built dll still
        hashes to what the report recorded AND that dll really is our assembly;
      * it never writes outside <game>\mods\STS2Portrait - the id is pinned in this script
        (card A19b) and the manifest, the report and the artifact must all agree with it,
        so editing a JSON key cannot redirect the deployment to another mod;
      * it never copies a dependency: the allow-list is our own files only (dll, manifest
        and, with -WithPdb, our pdb). deps.json is excluded on purpose - the loader uses
        AssemblyLoadContext.LoadFromAssemblyPath and does not read it (docs/MODDING-NOTES.md);
      * it never deletes a directory and never kills a process; if the game is running, or a
        destination file is locked, it stops;
      * it never installs or touches any other mod folder.

    Before overwriting an existing destination file it is copied to a backup folder inside
    this project's logs directory, timestamped per run.

    Exit codes:
      0  planned (dry-run) or deployed successfully
      1  input validation failed (manifest / project layout / report unreadable)
      2  build report rejected (not success, wrong/absent schema, noisy, stale versus the
         recorded input hashes, artifact missing or drifted, or identity mismatch)
      3  unsafe to write (game running, or a destination file is locked)
      4  target/identity path rejected (not a real game install, not exactly
         <game>\mods\STS2Portrait, reparse point, or the manifest id is not this project's)
      5  copy performed but read-back verification failed

    Windows PowerShell 5.1 compatible. Reads JSON with -Encoding UTF8 on purpose: 5.1 would
    otherwise decode our UTF-8 reports as ANSI and fail on the non-ASCII user name in paths.
#>
[CmdletBinding()]
param(
    # Actually perform the copy. Without it the script only reports what it WOULD do.
    [switch]$Apply,

    # Include our .pdb in the copy set (debug symbols are optional; the game does not need them).
    [switch]$WithPdb,

    # Game install root. Resolution order: this parameter, then STS2_GAME_DIR, then the
    # same literal the project file falls back to (ASCII-only, safe to hardcode).
    [string]$GameDir,

    # Build report produced by tools\build.ps1.
    [string]$ReportPath,

    # Where overwritten destination files are preserved.
    [string]$BackupDir,

    # Where deploy-report.json (and any transcript the caller tees) goes.
    # Defaults to <project>\logs. Tests can point this at a throwaway folder so the
    # repository's own deploy record stays meaningful.
    [string]$LogsDir
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

function Get-FileSha256 {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-FileUnlocked {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $true }
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $fs.Close()
        return $true
    } catch {
        return $false
    }
}

# ---------------------------------------------------------------------------
# Stage 0: layout, then the report/plan state holders.
# Rpt prefix everywhere: PowerShell names are case-insensitive, so $LogPath would be the
# same variable as a local $logPath (this exact bug cost A07 a debug cycle).
# ---------------------------------------------------------------------------
$startedAt = Get-Date
$toolsDir = $PSScriptRoot
$projectRoot = Split-Path -Parent $toolsDir

$RptStatus = 'planned'
$RptReason = $null
$RptApplied = $false
$RptTarget = $null
$RptItems = @()
$RptBackups = @()

$manifestPath = Join-Path $projectRoot 'mod_manifest.json'
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot 'logs\build-report.json'
}

if ([string]::IsNullOrWhiteSpace($LogsDir)) { $LogsDir = Join-Path $projectRoot 'logs' }
if (-not (Test-Path -LiteralPath $LogsDir -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $LogsDir
}
$stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
$deployReportPath = Join-Path $LogsDir 'deploy-report.json'
# Suggested transcript path for the caller (this script writes only the JSON report;
# capture the console with  ... *>&1 | Tee-Object <this path>  when a transcript is needed).
$logPath = Join-Path $LogsDir ("deploy-{0}.log" -f $stamp)

function Write-DeployReport {
    $record = [ordered]@{
        status       = $RptStatus
        reason       = $RptReason
        mode         = $(if ($Apply) { 'apply' } else { 'dry-run' })
        started_at   = $startedAt.ToString('o')
        finished_at  = (Get-Date).ToString('o')
        build_report = $ReportPath
        target_dir   = $RptTarget
        items        = $RptItems
        backups      = $RptBackups
    }
    Write-Utf8NoBom -Path $deployReportPath -Text ($record | ConvertTo-Json -Depth 8)
    Write-Output ("report       : {0}" -f $deployReportPath)
    if ($RptStatus -ne 'success' -and $RptStatus -ne 'planned-ok') {
        Write-Output ("DEPLOY REFUSED [status={0}] {1}" -f $RptStatus, $RptReason)
    }
}

function Stop-Deploy {
    param([int]$Code, [string]$Status, [string]$Reason)
    $script:RptStatus = $Status
    $script:RptReason = $Reason
    Write-DeployReport
    exit $Code
}

# ---------------------------------------------------------------------------
# Stage 1: identity is PINNED here, then every source of identity must agree with it.
#
# Card A19b closes review F1. The previous version took $modId from whatever the
# manifest happened to say and then built the destination AND the path check out of
# that same value - a tautology that could only show "the target matches this id",
# never "this is still our mod". Editing one JSON key redirected a real deployment
# into another mod's folder (evidence: A18-review-evidence/wrong-id.log).
#
# The pin below is the project's own identity. It is deliberately NOT read from the
# manifest, the report, or the environment: those are the untrusted inputs being
# checked. Changing what this mod is called therefore requires editing this script,
# which is the intended amount of friction.
# ---------------------------------------------------------------------------
$ExpectedModId = 'STS2Portrait'
$ExpectedAssemblyName = 'STS2Portrait'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Stop-Deploy -Code 1 -Status 'input' -Reason ("mod_manifest.json not found at '{0}'" -f $manifestPath)
}
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
try { $manifest = $manifestText | ConvertFrom-Json } catch {
    Stop-Deploy -Code 1 -Status 'input' -Reason ("mod_manifest.json is not valid JSON: {0}" -f $_.Exception.Message)
}
$modId = [string]$manifest.id
if ([string]::IsNullOrWhiteSpace($modId)) {
    Stop-Deploy -Code 1 -Status 'input' -Reason 'mod_manifest.json has no id; the loader derives the dll filename from it (ModManager.cs L800), so refusing to guess.'
}
# Identity agreement, check 1 of 3: the manifest must still describe THIS mod.
if (-not [string]::Equals($modId, $ExpectedModId, [System.StringComparison]::Ordinal)) {
    Stop-Deploy -Code 4 -Status 'identity-mismatch' -Reason ("mod_manifest.json id is '{0}' but this project deploys '{1}'; a manifest that renames the mod must not redirect the target folder" -f $modId, $ExpectedModId)
}

$expectedDllName = $modId + '.dll'
$expectedPdbName = $modId + '.pdb'

# ---------------------------------------------------------------------------
# Stage 2: accept the build report only if it is green AND still matches the sources.
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    Stop-Deploy -Code 1 -Status 'input' -Reason ("build report not found at '{0}'; run tools\build.ps1 first" -f $ReportPath)
}
$reportText = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
try { $report = $reportText | ConvertFrom-Json } catch {
    Stop-Deploy -Code 1 -Status 'input' -Reason ("build report is not valid JSON (read it with -Encoding UTF8): {0}" -f $_.Exception.Message)
}

if ($report.status -ne 'success') {
    Stop-Deploy -Code 2 -Status 'report-not-success' -Reason ("build report status is '{0}' (reason: {1})" -f $report.status, $report.reason)
}
if ($report.errors -ne 0 -or $report.warnings -ne 0) {
    Stop-Deploy -Code 2 -Status 'report-not-clean' -Reason ("build report says errors={0} warnings={1}; deploying a noisy build is not allowed" -f $report.errors, $report.warnings)
}

# ---------------------------------------------------------------------------
# Stage 2b: require the A19e/A19f input-fingerprint schema (card A19f).
# A report written by older build.ps1 versions cannot be trusted to gate a deployment.
# Reject it loudly and require rebuild, rather than falling back to a weaker check.
# ---------------------------------------------------------------------------
$schemaVersion = [string]$report.schema_version
if ([string]::IsNullOrWhiteSpace($schemaVersion)) {
    Stop-Deploy -Code 2 -Status 'report-schema-missing' -Reason "build report has no schema_version, so it predates the card A19e input fingerprint; re-run tools\build.ps1 and use the new report"
}
if ($schemaVersion -ne '3.0') {
    Stop-Deploy -Code 2 -Status 'report-schema-unsupported' -Reason ("build report schema_version is '{0}' but this script understands only '3.0'; re-run tools\build.ps1" -f $schemaVersion)
}
if ($null -eq $report.inputs -or @($report.inputs).Count -eq 0) {
    Stop-Deploy -Code 2 -Status 'report-inputs-empty' -Reason 'build report has schema_version 3.0 but an empty inputs array; the fingerprint is unusable, refusing to deploy'
}
if ($null -eq $report.evaluation_contract) {
    Stop-Deploy -Code 2 -Status 'report-contract-missing' -Reason 'build report is missing evaluation_contract; re-run tools\build.ps1'
}

# Identity agreement, check 2 of 3: the report must describe THIS project and assembly.
$reportAssembly = [string]$report.assembly_name
if (-not [string]::Equals($reportAssembly, $ExpectedAssemblyName, [System.StringComparison]::Ordinal)) {
    Stop-Deploy -Code 2 -Status 'report-identity-mismatch' -Reason ("build report assembly_name is '{0}' but this project deploys '{1}'" -f $reportAssembly, $ExpectedAssemblyName)
}
$reportProjectRoot = [System.IO.Path]::GetFullPath([string]$report.project_root)
$actualProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
if (-not [string]::Equals($reportProjectRoot.TrimEnd('\'), $actualProjectRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    Stop-Deploy -Code 2 -Status 'report-identity-mismatch' -Reason ("build report project_root is '{0}' but this script deploys the project at '{1}'; the report belongs to a different project" -f $reportProjectRoot, $actualProjectRoot)
}

$builtDll = [string]$report.artifact.path
$builtHash = [string]$report.artifact.sha256
if (-not (Test-Path -LiteralPath $builtDll -PathType Leaf)) {
    Stop-Deploy -Code 2 -Status 'artifact-missing' -Reason ("build report points at '{0}' which no longer exists" -f $builtDll)
}
$nowHash = Get-FileSha256 -Path $builtDll
if ($nowHash -ne $builtHash) {
    Stop-Deploy -Code 2 -Status 'artifact-drift' -Reason ("dll hash {0} no longer matches the build report ({1})" -f $nowHash, $builtHash)
}
# Identity agreement, check 3 of 3: the artifact we are about to copy must be OUR
# assembly, not merely a file that happens to sit at the reported path.
try {
    $artifactAssemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($builtDll).Name
} catch {
    Stop-Deploy -Code 2 -Status 'artifact-unreadable' -Reason ("cannot read assembly identity from '{0}': {1}" -f $builtDll, $_.Exception.Message)
}
if (-not [string]::Equals($artifactAssemblyName, $ExpectedAssemblyName, [System.StringComparison]::Ordinal)) {
    Stop-Deploy -Code 2 -Status 'artifact-identity-mismatch' -Reason ("'{0}' contains assembly '{1}', not '{2}'" -f $builtDll, $artifactAssemblyName, $ExpectedAssemblyName)
}

# ---------------------------------------------------------------------------
# Stage 2c: re-evaluate the current project and compare against recorded inputs & config.
#
# Card A19f closes review R1 and R2. Rather than only iterating recorded inputs,
# deploy dynamically evaluates the current project via MSBuild to catch:
#   1. New source or import files added since the recorded build (R1)
#   2. Removed source or import files since the recorded build
#   3. Modified file contents (even if mtime was preserved)
#   4. Effective configuration property drifts such as AssemblyVersion (R2)
# ---------------------------------------------------------------------------
$projCandidates = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
if ($projCandidates.Count -ne 1) {
    Stop-Deploy -Code 1 -Status 'input' -Reason ("expected exactly one .csproj in '{0}', found {1}" -f $projectRoot, $projCandidates.Count)
}
$currentProjectFile = $projCandidates[0].FullName

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Stop-Deploy -Code 1 -Status 'input' -Reason 'dotnet was not found on PATH; this script never downloads tooling.'
}
$currentDotnetSdk = (& $dotnet.Source --version | Out-String).Trim()
if ($currentDotnetSdk -ne [string]$report.dotnet_sdk) {
    Stop-Deploy -Code 2 -Status 'report-sdk-mismatch' -Reason ("dotnet SDK changed since build (report='{0}', current='{1}'); rebuild first" -f [string]$report.dotnet_sdk, $currentDotnetSdk)
}

$queryArgs = @(
    'msbuild',
    '-getProperty:TargetFramework',
    '-getProperty:TargetPlatformIdentifier',
    '-getProperty:TargetFrameworks',
    '-getProperty:AssemblyName',
    '-getProperty:Configuration',
    '-getProperty:OutputPath',
    '-getProperty:IntermediateOutputPath',
    '-getProperty:BaseIntermediateOutputPath',
    '-getProperty:Nullable',
    '-getProperty:ImplicitUsings',
    '-getProperty:TreatWarningsAsErrors',
    '-getProperty:WarningLevel',
    '-getProperty:CheckForOverflowUnderflow',
    '-getProperty:Deterministic',
    '-getProperty:LangVersion',
    '-getProperty:DefineConstants',
    '-getProperty:AssemblyVersion',
    '-getProperty:FileVersion',
    '-getProperty:Version',
    '-getProperty:RitsuLibRefsProps',
    '-getProperty:RitsuLibReferenceTarget',
    '-getProperty:Sts2GameDir',
    '-getProperty:DirectoryBuildPropsPath',
    '-getProperty:DirectoryBuildTargetsPath',
    '-getProperty:DirectoryPackagesPropsPath',
    '-getProperty:CustomBeforeDirectoryBuildProps',
    '-getProperty:CustomAfterDirectoryBuildProps',
    '-getProperty:CustomBeforeDirectoryBuildTargets',
    '-getProperty:CustomAfterDirectoryBuildTargets',
    '-getProperty:NetCoreRoot',
    '-getProperty:MSBuildExtensionsPath',
    '-getProperty:MSBuildSDKsPath',
    '-getItem:Compile',
    '-getItem:Reference',
    '-getItem:ProjectReference',
    '-getItem:PackageReference',
    $currentProjectFile
)
$queryOut = & $dotnet.Source @queryArgs 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Stop-Deploy -Code 2 -Status 'eval-failed' -Reason ("dotnet msbuild query failed during deploy check with code {0}: {1}" -f $LASTEXITCODE, $queryOut)
}
try {
    $mb = $queryOut | ConvertFrom-Json
} catch {
    Stop-Deploy -Code 2 -Status 'eval-failed' -Reason ("Failed to parse msbuild json during deploy check: {0}" -f $_.Exception.Message)
}

$props = $mb.Properties
$tfms = [string]$props.TargetFrameworks
if (-not [string]::IsNullOrWhiteSpace($tfms)) {
    Stop-Deploy -Code 2 -Status 'eval-unsupported' -Reason ("Multi-targeting (TargetFrameworks='{0}') is not supported." -f $tfms)
}
if ($null -ne $mb.Items -and $null -ne $mb.Items.ProjectReference -and $mb.Items.ProjectReference.Count -gt 0) {
    Stop-Deploy -Code 2 -Status 'eval-unsupported' -Reason 'ProjectReference is not supported; project must be standalone library.'
}
if ($null -ne $mb.Items -and $null -ne $mb.Items.PackageReference -and $mb.Items.PackageReference.Count -gt 0) {
    Stop-Deploy -Code 2 -Status 'eval-unsupported' -Reason 'PackageReference is not supported; project must be offline.'
}

$projDir = Split-Path -Parent $currentProjectFile
$projDirFull = [System.IO.Path]::GetFullPath($projDir)
$outDirRaw = [string]$props.OutputPath
$objDirRaw = [string]$props.IntermediateOutputPath
$baseObjDirRaw = [string]$props.BaseIntermediateOutputPath

$outFull = if ([System.IO.Path]::IsPathRooted($outDirRaw)) { [System.IO.Path]::GetFullPath($outDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $outDirRaw)) }
$objFull = if ([string]::IsNullOrWhiteSpace($objDirRaw)) { [System.IO.Path]::Combine($projDirFull, 'obj') } elseif ([System.IO.Path]::IsPathRooted($objDirRaw)) { [System.IO.Path]::GetFullPath($objDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $objDirRaw)) }
$baseObjFull = if ([string]::IsNullOrWhiteSpace($baseObjDirRaw)) { [System.IO.Path]::Combine($projDirFull, 'obj') } elseif ([System.IO.Path]::IsPathRooted($baseObjDirRaw)) { [System.IO.Path]::GetFullPath($baseObjDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $baseObjDirRaw)) }
$binFull = [System.IO.Path]::Combine($projDirFull, 'bin')

$netCoreRoot = if (-not [string]::IsNullOrWhiteSpace($props.NetCoreRoot)) { [System.IO.Path]::GetFullPath([string]$props.NetCoreRoot) } else { '' }
$msbuildExtPath = if (-not [string]::IsNullOrWhiteSpace($props.MSBuildExtensionsPath)) { [System.IO.Path]::GetFullPath([string]$props.MSBuildExtensionsPath) } else { '' }
$msbuildSdksPath = if (-not [string]::IsNullOrWhiteSpace($props.MSBuildSDKsPath)) { [System.IO.Path]::GetFullPath([string]$props.MSBuildSDKsPath) } else { '' }

$currentInputList = @()
$currentInputList += [pscustomobject]@{
    Path = [System.IO.Path]::GetFullPath($currentProjectFile)
    Kind = 'project'
}

$knownImportProps = @(
    [string]$props.DirectoryBuildPropsPath,
    [string]$props.DirectoryBuildTargetsPath,
    [string]$props.DirectoryPackagesPropsPath,
    [string]$props.CustomBeforeDirectoryBuildProps,
    [string]$props.CustomAfterDirectoryBuildProps,
    [string]$props.CustomBeforeDirectoryBuildTargets,
    [string]$props.CustomAfterDirectoryBuildTargets,
    [string]$props.RitsuLibRefsProps
)
foreach ($impProp in $knownImportProps) {
    if (-not [string]::IsNullOrWhiteSpace($impProp)) {
        $parts = $impProp -split ';'
        foreach ($part in $parts) {
            $trimmed = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                if (-not [System.IO.Path]::IsPathRooted($trimmed)) {
                    $trimmed = [System.IO.Path]::Combine($projDir, $trimmed)
                }
                $impFull = [System.IO.Path]::GetFullPath($trimmed)
                if (-not (Test-Path -LiteralPath $impFull -PathType Leaf)) {
                    Stop-Deploy -Code 2 -Status 'eval-import-missing' -Reason ("Imported file does not exist: '{0}'" -f $impFull)
                }
                $isSdk = $false
                if (-not [string]::IsNullOrWhiteSpace($netCoreRoot) -and $impFull.StartsWith($netCoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                if (-not [string]::IsNullOrWhiteSpace($msbuildExtPath) -and $impFull.StartsWith($msbuildExtPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                if (-not [string]::IsNullOrWhiteSpace($msbuildSdksPath) -and $impFull.StartsWith($msbuildSdksPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }

                if (-not $isSdk) {
                    $currentInputList += [pscustomobject]@{
                        Path = $impFull
                        Kind = 'import'
                    }
                }
            }
        }
    }
}

$tempPreprocess = [System.IO.Path]::GetTempFileName()
try {
    $prepOut = & $dotnet.Source msbuild "-preprocess:$tempPreprocess" $currentProjectFile 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tempPreprocess -PathType Leaf)) {
        $prepContent = [System.IO.File]::ReadAllText($tempPreprocess)
        $pattern = '(?m)^[ \t]*([A-Za-z]:\\[^\r\n]+)\r?\n[ \t]*={100,}'
        $matches = [regex]::Matches($prepContent, $pattern)
        foreach ($match in $matches) {
            $rawPath = $match.Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace($rawPath)) {
                $candFull = [System.IO.Path]::GetFullPath($rawPath)

                $isSdk = $false
                if (-not [string]::IsNullOrWhiteSpace($netCoreRoot) -and $candFull.StartsWith($netCoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                if (-not [string]::IsNullOrWhiteSpace($msbuildExtPath) -and $candFull.StartsWith($msbuildExtPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                if (-not [string]::IsNullOrWhiteSpace($msbuildSdksPath) -and $candFull.StartsWith($msbuildSdksPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }

                $isObjOrBin = $false
                if (-not [string]::IsNullOrWhiteSpace($objFull) -and $candFull.StartsWith($objFull, [System.StringComparison]::OrdinalIgnoreCase)) { $isObjOrBin = $true }
                if (-not [string]::IsNullOrWhiteSpace($binFull) -and $candFull.StartsWith($binFull, [System.StringComparison]::OrdinalIgnoreCase)) { $isObjOrBin = $true }
                if (-not [string]::IsNullOrWhiteSpace($baseObjFull) -and $candFull.StartsWith($baseObjFull, [System.StringComparison]::OrdinalIgnoreCase)) { $isObjOrBin = $true }

                $isProject = ($candFull -eq [System.IO.Path]::GetFullPath($currentProjectFile))

                if (-not $isSdk -and -not $isObjOrBin -and -not $isProject) {
                    if (-not (Test-Path -LiteralPath $candFull -PathType Leaf)) {
                        Stop-Deploy -Code 2 -Status 'eval-import-missing' -Reason ("Imported file discovered via preprocess does not exist: '{0}'" -f $candFull)
                    }
                    $currentInputList += [pscustomobject]@{
                        Path = $candFull
                        Kind = 'import'
                    }
                }
            }
        }
    } else {
        Stop-Deploy -Code 2 -Status 'eval-failed' -Reason ("dotnet msbuild -preprocess failed with code {0}: {1}" -f $LASTEXITCODE, $prepOut)
    }
} finally {
    if (Test-Path -LiteralPath $tempPreprocess) {
        [System.IO.File]::Delete($tempPreprocess)
    }
}

if ($null -ne $mb.Items -and $null -ne $mb.Items.Compile -and $mb.Items.Compile.Count -gt 0) {
    foreach ($c in $mb.Items.Compile) {
        $cPath = [string]$c.FullPath
        if ([string]::IsNullOrWhiteSpace($cPath)) {
            $cPath = [string]$c.Identity
            if (-not [System.IO.Path]::IsPathRooted($cPath)) {
                $cPath = [System.IO.Path]::Combine($projDir, $cPath)
            }
        }
        $cFull = [System.IO.Path]::GetFullPath($cPath)
        if (-not (Test-Path -LiteralPath $cFull -PathType Leaf)) {
            Stop-Deploy -Code 2 -Status 'input-missing' -Reason ("Compile item does not exist: '{0}'" -f $cFull)
        }
        $currentInputList += [pscustomobject]@{
            Path = $cFull
            Kind = 'compile'
        }
    }
} else {
    Stop-Deploy -Code 2 -Status 'eval-failed' -Reason 'No Compile items found in project'
}

if ($null -ne $mb.Items -and $null -ne $mb.Items.Reference) {
    foreach ($r in $mb.Items.Reference) {
        $h = [string]$r.HintPath
        if (-not [string]::IsNullOrWhiteSpace($h)) {
            if (-not [System.IO.Path]::IsPathRooted($h)) {
                $h = [System.IO.Path]::Combine($projDir, $h)
            }
            $hFull = [System.IO.Path]::GetFullPath($h)
            if (-not (Test-Path -LiteralPath $hFull -PathType Leaf)) {
                Stop-Deploy -Code 2 -Status 'input-missing' -Reason ("Reference HintPath does not exist: '{0}'" -f $hFull)
            }
            $currentInputList += [pscustomobject]@{
                Path = $hFull
                Kind = 'reference'
            }
        } else {
            Stop-Deploy -Code 2 -Status 'eval-unsupported' -Reason ("Reference '{0}' is missing HintPath." -f [string]$r.Identity)
        }
    }
}

# Deduplicate current input paths case-insensitively
$currentDict = [System.Collections.Generic.Dictionary[string, pscustomobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($item in $currentInputList) {
    if (-not $currentDict.ContainsKey($item.Path)) {
        $currentDict.Add($item.Path, $item)
    }
}

$recordedInputs = @($report.inputs)
$recordedDict = [System.Collections.Generic.Dictionary[string, pscustomobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($rec in $recordedInputs) {
    $p = [string]$rec.path
    if (-not $recordedDict.ContainsKey($p)) {
        $recordedDict.Add($p, $rec)
    }
}

# Check for new inputs added to project since build (closes R1)
foreach ($curPath in $currentDict.Keys) {
    if (-not $recordedDict.ContainsKey($curPath)) {
        Stop-Deploy -Code 2 -Status 'input-drift' -Reason ("Current project has new input '{0}' (kind={1}) that was not in recorded build report; rebuild first" -f $curPath, $currentDict[$curPath].Kind)
    }
}

# Check for recorded inputs removed from project since build
foreach ($recPath in $recordedDict.Keys) {
    if (-not $currentDict.ContainsKey($recPath)) {
        Stop-Deploy -Code 2 -Status 'input-drift' -Reason ("Recorded build input '{0}' is no longer present in evaluated project; rebuild first" -f $recPath)
    }
}

# Check for content drift on every recorded input
foreach ($rec in $recordedInputs) {
    $inPath = [string]$rec.path
    $inHash = [string]$rec.sha256
    $inKind = [string]$rec.kind
    if (-not (Test-Path -LiteralPath $inPath -PathType Leaf)) {
        Stop-Deploy -Code 2 -Status 'input-missing' -Reason ("Build input '{0}' (kind={1}) does not exist on disk; rebuild first" -f $inPath, $inKind)
    }
    $currentHash = Get-FileSha256 -Path $inPath
    if (-not [string]::Equals($currentHash, $inHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-Deploy -Code 2 -Status 'input-drift' -Reason ("Build input '{0}' (kind={1}) changed after recorded build (report={2}, now={3}); rebuild first" -f $inPath, $inKind, $inHash, $currentHash)
    }
}

# Check for effective configuration property changes (closes R2)
if ($null -ne $report.configuration_properties) {
    $curProps = [ordered]@{
        TargetFramework           = [string]$props.TargetFramework
        TargetPlatformIdentifier  = [string]$props.TargetPlatformIdentifier
        AssemblyName              = [string]$props.AssemblyName
        Configuration             = [string]$props.Configuration
        OutputPath                = [string]$props.OutputPath
        IntermediateOutputPath    = [string]$props.IntermediateOutputPath
        Nullable                  = [string]$props.Nullable
        ImplicitUsings            = [string]$props.ImplicitUsings
        TreatWarningsAsErrors     = [string]$props.TreatWarningsAsErrors
        WarningLevel              = [string]$props.WarningLevel
        CheckForOverflowUnderflow = [string]$props.CheckForOverflowUnderflow
        Deterministic             = [string]$props.Deterministic
        LangVersion               = [string]$props.LangVersion
        DefineConstants           = [string]$props.DefineConstants
        AssemblyVersion           = [string]$props.AssemblyVersion
        FileVersion               = [string]$props.FileVersion
        Version                   = [string]$props.Version
        RitsuLibRefsProps         = [string]$props.RitsuLibRefsProps
        RitsuLibReferenceTarget   = [string]$props.RitsuLibReferenceTarget
        Sts2GameDir               = [string]$props.Sts2GameDir
        DirectoryBuildPropsPath   = [string]$props.DirectoryBuildPropsPath
        DirectoryBuildTargetsPath = [string]$props.DirectoryBuildTargetsPath
        DirectoryPackagesPropsPath= [string]$props.DirectoryPackagesPropsPath
    }
    foreach ($propName in $curProps.Keys) {
        $curVal = [string]$curProps[$propName]
        $repVal = [string]$report.configuration_properties.$propName
        if ($curVal -ne $repVal) {
            Stop-Deploy -Code 2 -Status 'config-drift' -Reason ("Configuration property '{0}' changed after build (report='{1}', now='{2}'); rebuild first" -f $propName, $repVal, $curVal)
        }
    }
} else {
    Stop-Deploy -Code 2 -Status 'report-config-missing' -Reason 'build report is missing configuration_properties; re-run tools\build.ps1'
}

# Diagnostic info: check if any file has newer mtime even though hashes match
$buildFinished = [DateTime]::Parse([string]$report.finished_at)
$newer = @($recordedInputs | Where-Object {
    (Get-Item -LiteralPath $_.path).LastWriteTimeUtc -gt $buildFinished.ToUniversalTime()
})
if ($newer.Count -gt 0) {
    Write-Output ("note         : {0} input file(s) carry a timestamp newer than the recorded build, but all {1} recorded input hashes and configurations still match - not refusing on that basis" -f $newer.Count, $recordedInputs.Count)
}

# ---------------------------------------------------------------------------
# Stage 3: resolve and constrain the destination.
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    if (-not [string]::IsNullOrWhiteSpace($env:STS2_GAME_DIR)) {
        $GameDir = $env:STS2_GAME_DIR
    } else {
        $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
    }
}
$gameRootFull = [System.IO.Path]::GetFullPath($GameDir)

# Guard 0: ensure log and backup directories are distinct from the game root.
$logsDirFull = [System.IO.Path]::GetFullPath($LogsDir)
if ($logsDirFull.StartsWith($gameRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("logs dir '{0}' cannot be located inside the game directory '{1}'" -f $logsDirFull, $gameRootFull)
}
if (-not [string]::IsNullOrWhiteSpace($BackupDir)) {
    $backupDirFull = [System.IO.Path]::GetFullPath($BackupDir)
    if ($backupDirFull.StartsWith($gameRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("backup dir '{0}' cannot be located inside the game directory '{1}'" -f $backupDirFull, $gameRootFull)
    }
}

# The destination is built from the PINNED id, never from the manifest or the report.
$targetDir = [System.IO.Path]::Combine($gameRootFull, 'mods', $ExpectedModId)

# Guard 1: game root must exist, be a regular directory (not a junction/symlink), and contain SlayTheSpire2.exe.
$gameRootItem = Get-Item -LiteralPath $gameRootFull -Force -ErrorAction SilentlyContinue
if ($null -eq $gameRootItem -or -not $gameRootItem.PSIsContainer) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("game root '{0}' is not an existing directory" -f $gameRootFull)
}
if (($gameRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("game root '{0}' is a reparse point (junction/symlink); refusing to deploy to reparse target" -f $gameRootFull)
}
if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($gameRootFull, 'SlayTheSpire2.exe')) -PathType Leaf)) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("'{0}' has no SlayTheSpire2.exe; this is not a game root, refusing to deploy" -f $gameRootFull)
}

# Guard 2: the path shape must be exactly <game>\mods\<pinned id>, nothing deeper or elsewhere.
$modsDir = [System.IO.Path]::GetDirectoryName($targetDir)
if ((Split-Path -Leaf $modsDir) -ne 'mods' -or (Split-Path -Leaf $targetDir) -ne $ExpectedModId) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("resolved target '{0}' is not of the form <game>\mods\{1}" -f $targetDir, $ExpectedModId)
}
# Guard 3: never write next to another mod - the target's parent must contain only mod folders.
if ($modsDir -ne [System.IO.Path]::Combine($gameRootFull, 'mods')) {
    Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("target is not directly under the game's mods folder: {0}" -f $targetDir)
}

# Guard 4 (card A19g): ancestor mods directory must be a REAL directory, not a junction/symlink.
# This prevents bypassing target checks by making the parent <game>\mods a reparse point (review R3).
if (Test-Path -LiteralPath $modsDir) {
    $modsItem = Get-Item -LiteralPath $modsDir -Force -ErrorAction SilentlyContinue
    if ($null -ne $modsItem) {
        if (-not $modsItem.PSIsContainer) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("mods directory '{0}' exists but is not a directory" -f $modsDir)
        }
        if (($modsItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("mods directory '{0}' is a reparse point (junction/symlink); refusing to deploy through linked folder" -f $modsDir)
        }
        $modsResolved = [System.IO.Path]::GetFullPath($modsItem.FullName)
        if (-not [string]::Equals($modsResolved.TrimEnd('\'), $modsDir.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("mods directory '{0}' resolves to '{1}', which is not the intended target" -f $modsDir, $modsResolved)
        }
    }
}

# Guard 5: the destination mod directory, if it exists, must be a REAL directory under the game root.
if (Test-Path -LiteralPath $targetDir) {
    $targetItem = Get-Item -LiteralPath $targetDir -Force -ErrorAction SilentlyContinue
    if ($null -ne $targetItem) {
        if (-not $targetItem.PSIsContainer) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("target path '{0}' exists but is not a directory" -f $targetDir)
        }
        if (($targetItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("'{0}' is a reparse point (junction/symlink); refusing to write through it" -f $targetDir)
        }
        $targetResolved = [System.IO.Path]::GetFullPath($targetItem.FullName)
        if (-not [string]::Equals($targetResolved.TrimEnd('\'), $targetDir.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("'{0}' resolves to '{1}', which is not the intended target" -f $targetDir, $targetResolved)
        }
    }
}

$script:RptTarget = $targetDir
Write-Output ("mod id       : {0} (pinned; verified against manifest, report and artifact)" -f $ExpectedModId)
Write-Output ("mode         : {0}" -f $(if ($Apply) { 'APPLY' } else { 'dry-run (pass -Apply to write)' }))
Write-Output ("target dir   : {0}" -f $targetDir)

# ---------------------------------------------------------------------------
# Stage 4: build the copy plan from the allow-list.
# ---------------------------------------------------------------------------
$plan = @()
$plan += [pscustomobject]@{
    Name = $expectedDllName
    Source = $builtDll
    Optional = $false
}
$plan += [pscustomobject]@{
    Name = 'mod_manifest.json'
    Source = $manifestPath
    Optional = $false
}
if ($WithPdb) {
    $plan += [pscustomobject]@{
        Name = $expectedPdbName
        Source = [System.IO.Path]::Combine((Split-Path -Parent $builtDll), $expectedPdbName)
        Optional = $true
    }
}
# Anything else that might be sitting in the output folder (deps.json, stray framework
# copies) is deliberately NOT part of the plan.
$explicitlySkipped = @(Get-ChildItem -LiteralPath (Split-Path -Parent $builtDll) -File |
    Where-Object { $Name = $_.Name; -not (@($plan | ForEach-Object { $_.Name }) -contains $Name) } |
    ForEach-Object { $_.Name })

foreach ($p in $plan) {
    if (-not (Test-Path -LiteralPath $p.Source -PathType Leaf)) {
        if ($p.Optional) { continue }
        Stop-Deploy -Code 1 -Status 'input' -Reason ("allow-list source missing: '{0}'" -f $p.Source)
    }
    $destPath = [System.IO.Path]::Combine($targetDir, $p.Name)
    $destExists = (Test-Path -LiteralPath $destPath)
    if ($destExists) {
        $destItem = Get-Item -LiteralPath $destPath -Force -ErrorAction SilentlyContinue
        if ($null -ne $destItem) {
            if ($destItem.PSIsContainer) {
                Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("destination '{0}' exists but is a directory, not a regular file" -f $destPath)
            }
            if (($destItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Deploy -Code 4 -Status 'target-rejected' -Reason ("destination file '{0}' is a reparse point (symlink); refusing to overwrite through link" -f $destPath)
            }
        }
    }
    $item = [ordered]@{
        name          = $p.Name
        source        = $p.Source
        source_bytes  = (Get-Item -LiteralPath $p.Source).Length
        source_sha256 = Get-FileSha256 -Path $p.Source
        destination   = $destPath
        existed_before= $destExists
        deployed_sha256 = $null
        verified      = $false
    }
    $RptItems += @($item)
    Write-Output ("  item       : {0}  {1} B  sha256={2}  exists_at_target={3}" -f $item.name, $item.source_bytes, $item.source_sha256.Substring(0,16), $item.existed_before)
}
if ($explicitlySkipped.Count -gt 0) {
    Write-Output ("  NOT copied : {0}" -f ($explicitlySkipped -join ', '))
}

# ---------------------------------------------------------------------------
# Stage 5: safety probes (running game, locked files) - observe, never interfere.
# ---------------------------------------------------------------------------
$procs = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue | Where-Object { $_.Threads.Count -gt 0 })
if ($procs.Count -gt 0) {
    Stop-Deploy -Code 3 -Status 'game-running' -Reason ("SlayTheSpire2 is running (pid {0}); this script never kills processes" -f (($procs | ForEach-Object { $_.Id }) -join '/'))
}
foreach ($item in $RptItems) {
    if ($item.existed_before -and -not (Test-FileUnlocked -Path $item.destination)) {
        Stop-Deploy -Code 3 -Status 'file-locked' -Reason ("destination is locked: {0}" -f $item.destination)
    }
}

if (-not $Apply) {
    $script:RptStatus = 'planned-ok'
    Write-DeployReport
    Write-Output 'DRY-RUN OK - nothing was written. Re-run with -Apply to deploy (that is card A09).'
    exit 0
}

# ---------------------------------------------------------------------------
# Stage 6: backup, copy, read back. No directory deletion anywhere.
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $targetDir
}
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $BackupDir = Join-Path $LogsDir ("deploy-backup-{0}" -f $stamp)
}

for ($i = 0; $i -lt $RptItems.Count; $i++) {
    $item = $RptItems[$i]
    $dest = $item.destination
    if ($item.existed_before) {
        if (-not (Test-Path -LiteralPath $BackupDir -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $BackupDir
        }
        $bk = Join-Path $BackupDir $item.name
        $null = Copy-Item -LiteralPath $dest -Destination $bk -Force
        $RptBackups += @([ordered]@{ file = $item.name; backup = $bk; sha256 = (Get-FileSha256 -Path $bk) })
        Write-Output ("  backed up  : {0} -> {1}" -f $dest, $bk)
    }
    $null = Copy-Item -LiteralPath $item.source -Destination $dest -Force
    $after = Get-FileSha256 -Path $dest
    $ok = ($after -eq $item.source_sha256)
    $RptItems[$i]['deployed_sha256'] = $after
    $RptItems[$i]['verified'] = $ok
    Write-Output ("  deployed   : {0}  read-back sha256={1}  match={2}" -f $dest, $after.Substring(0,16), $ok)
    if (-not $ok) {
        $script:RptApplied = $true
        Stop-Deploy -Code 5 -Status 'readback-mismatch' -Reason ("read-back of {0} differs from the source" -f $dest)
    }
}

$script:RptApplied = $true
$script:RptStatus = 'success'
Write-DeployReport
Write-Output ("applied      : {0} file(s) into {1}" -f $RptItems.Count, $targetDir)
Write-Output 'DEPLOY OK (read-back verified; no directory deleted, no process touched)'
exit 0
