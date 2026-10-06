<#
.SYNOPSIS
    Single-purpose build script for the STS2Portrait mod: compile only.

.DESCRIPTION
    Card A07 of docs/Plan. This script does exactly one job - run the manual build
    baseline proven in reports/A03.md and reports/A05.md - and deliberately never
    deploys, never launches the game, and never restores NuGet packages (the project
    has zero package references; a restore could reach the network, which this project
    forbids without explicit user approval).

    Why -t:Rebuild by default: the acceptance rule "do not count a stale artifact as
    this run's success" (A07 step 3) cannot be satisfied by an incremental build that
    MSBuild decides to skip, because a skipped compile leaves the previous dll in place
    with its old timestamp. Forcing a rebuild turns "this artifact came from this
    invocation" into something we can actually check, at the cost of a few seconds.

    Outputs, all under <projectRoot>\logs (never next to the sources):
      build-<timestamp>.log   raw console output of this run
      build-report.json       machine-readable result of the most recent run

    Every exit path except a pure argument typo writes build-report.json with an explicit
    "status" field, and a failing run never writes status=success. That removes the
    stale-success hazard: whatever this file says, it describes the last attempt.

    Exit codes:
      0  build succeeded and every post-condition held
      1  input validation failed (project file missing / layout ambiguous / dotnet absent)
      2  dotnet build returned non-zero
      3  expected artifact missing, or not produced by this run (stale), or MSBuild
         metadata could not be read
      4  output hygiene violated (a host-provided assembly was copied next to our dll)

    Kept readable by Windows PowerShell 5.1 as well as PowerShell 7. No path that is
    specific to this machine is hardcoded: everything is derived from $PSScriptRoot.
#>
[CmdletBinding()]
param(
    # Path to the project file. Omit it to use the single .csproj that sits next to
    # this script's parent directory. It can be pointed at a nonexistent file to
    # exercise the failure path without touching the real project (A07 step 4).
    [string]$Project,

    # Where the timestamped log and the JSON report are written.
    [string]$LogsDir
)

$ErrorActionPreference = 'Stop'

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    # Out-File -Encoding utf8 adds a BOM under Windows PowerShell 5.1, which trips
    # JSON readers; write the bytes explicitly instead.
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# ---------------------------------------------------------------------------
# Stage 0: locate ourselves and the log directory. Everything after this point
# can report a failure, so nothing that can fail happens before it.
# ---------------------------------------------------------------------------
$startedAt = Get-Date
$toolsDir = $PSScriptRoot
$projectRoot = Split-Path -Parent $toolsDir

if ([string]::IsNullOrWhiteSpace($LogsDir)) {
    $LogsDir = Join-Path $projectRoot 'logs'
}
if (-not (Test-Path -LiteralPath $LogsDir -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $LogsDir
}

$stamp = $startedAt.ToString('yyyyMMdd-HHmmss')
$logPath = Join-Path $LogsDir ("build-{0}.log" -f $stamp)
$reportPath = Join-Path $LogsDir 'build-report.json'

# State that the reporter reads; the build stage fills it in as it goes.
# NOTE the Rpt prefix: PowerShell variable names are case-INsensitive, so a state slot
# called $LogPath would silently be the same variable as the local $logPath above and
# null it out. Every report field therefore gets an explicit Rpt-prefixed holder.
$RptProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
$RptProject = $null
$RptAssemblyName = $null
$RptTargetFramework = $null
$RptConfiguration = $null
$RptConfigProps = $null
$RptContract = $null
$RptInputs = $null
$RptCommand = $null
$RptBuildExit = $null
$RptErrors = 0
$RptWarnings = 0
$RptLogPath = $null
$RptSdk = $null

function Write-Report {
    param([string]$Status, [string]$Reason, $Artifact)

    $record = [ordered]@{
        schema_version           = '3.0'
        status                   = $Status
        reason                   = $Reason
        started_at               = $startedAt.ToString('o')
        finished_at              = (Get-Date).ToString('o')
        project_root             = $RptProjectRoot
        project                  = $RptProject
        assembly_name            = $RptAssemblyName
        target_framework         = $RptTargetFramework
        configuration            = $RptConfiguration
        command                  = $RptCommand
        dotnet_sdk               = $RptSdk
        build_exit               = $RptBuildExit
        errors                   = $RptErrors
        warnings                 = $RptWarnings
        log_path                 = $RptLogPath
        evaluation_contract      = $RptContract
        configuration_properties = $RptConfigProps
        inputs                   = $RptInputs
        artifact                 = $Artifact
    }
    Write-Utf8NoBom -Path $reportPath -Text ($record | ConvertTo-Json -Depth 8)
    Write-Output ("report       : {0}" -f $reportPath)
    if ($Status -ne 'success') {
        Write-Output ("BUILD FAIL [status={0}] {1}" -f $Status, $Reason)
    }
}

function Stop-Build {
    param([int]$Code, [string]$Status, [string]$Reason, $Artifact)
    Write-Report -Status $Status -Reason $Reason -Artifact $Artifact
    exit $Code
}

function Get-Artifact {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $fi = Get-Item -LiteralPath $Path
    $info = [ordered]@{
        path       = $Path
        bytes      = $fi.Length
        last_write = $fi.LastWriteTime.ToString('o')
        sha256     = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        files      = @()
    }
    return $info
}

function Get-BuildInputs {
    param(
        [string]$ProjectFile,
        [string]$DotnetCmd
    )

    # Query comprehensive MSBuild project properties and items in a single invocation.
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
        $ProjectFile
    )
    $queryOut = & $DotnetCmd @queryArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        return @{
            Success = $false
            Reason  = ("dotnet msbuild query failed with code {0}: {1}" -f $LASTEXITCODE, $queryOut)
        }
    }

    try {
        $mb = $queryOut | ConvertFrom-Json
    } catch {
        return @{
            Success = $false
            Reason  = ("Failed to parse msbuild json: {0}" -f $_.Exception.Message)
        }
    }

    $props = $mb.Properties
    $tfm = [string]$props.TargetFramework
    $tfms = [string]$props.TargetFrameworks
    $asm = [string]$props.AssemblyName
    $cfg = [string]$props.Configuration
    $outDirRaw = [string]$props.OutputPath
    $objDirRaw = [string]$props.IntermediateOutputPath
    $baseObjDirRaw = [string]$props.BaseIntermediateOutputPath

    # Supported contract enforcement (A19e / R2)
    if (-not [string]::IsNullOrWhiteSpace($tfms)) {
        return @{
            Success = $false
            Reason  = ("Multi-targeting (TargetFrameworks='{0}') is not supported. Project must target a single framework." -f $tfms)
        }
    }
    if ($tfm -ne 'net9.0') {
        return @{
            Success = $false
            Reason  = ("TargetFramework '{0}' is not supported. Only 'net9.0' is supported." -f $tfm)
        }
    }
    if ([string]::IsNullOrWhiteSpace($asm) -or [string]::IsNullOrWhiteSpace($cfg) -or [string]::IsNullOrWhiteSpace($outDirRaw)) {
        return @{
            Success = $false
            Reason  = 'dotnet msbuild query missing AssemblyName, Configuration, or OutputPath'
        }
    }
    if ($null -ne $mb.Items -and $null -ne $mb.Items.ProjectReference -and $mb.Items.ProjectReference.Count -gt 0) {
        return @{
            Success = $false
            Reason  = 'ProjectReference is not supported; mod project must be a single standalone library.'
        }
    }
    if ($null -ne $mb.Items -and $null -ne $mb.Items.PackageReference -and $mb.Items.PackageReference.Count -gt 0) {
        return @{
            Success = $false
            Reason  = 'PackageReference is not supported; mod project relies strictly on local game/mod assemblies and offline refs.'
        }
    }

    $projDir = Split-Path -Parent $ProjectFile
    $projDirFull = [System.IO.Path]::GetFullPath($projDir)

    $outFull = if ([System.IO.Path]::IsPathRooted($outDirRaw)) { [System.IO.Path]::GetFullPath($outDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $outDirRaw)) }
    $objFull = if ([string]::IsNullOrWhiteSpace($objDirRaw)) { [System.IO.Path]::Combine($projDirFull, 'obj') } elseif ([System.IO.Path]::IsPathRooted($objDirRaw)) { [System.IO.Path]::GetFullPath($objDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $objDirRaw)) }
    $baseObjFull = if ([string]::IsNullOrWhiteSpace($baseObjDirRaw)) { [System.IO.Path]::Combine($projDirFull, 'obj') } elseif ([System.IO.Path]::IsPathRooted($baseObjDirRaw)) { [System.IO.Path]::GetFullPath($baseObjDirRaw) } else { [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projDir, $baseObjDirRaw)) }
    $binFull = [System.IO.Path]::Combine($projDirFull, 'bin')

    $netCoreRoot = if (-not [string]::IsNullOrWhiteSpace($props.NetCoreRoot)) { [System.IO.Path]::GetFullPath([string]$props.NetCoreRoot) } else { '' }
    $msbuildExtPath = if (-not [string]::IsNullOrWhiteSpace($props.MSBuildExtensionsPath)) { [System.IO.Path]::GetFullPath([string]$props.MSBuildExtensionsPath) } else { '' }
    $msbuildSdksPath = if (-not [string]::IsNullOrWhiteSpace($props.MSBuildSDKsPath)) { [System.IO.Path]::GetFullPath([string]$props.MSBuildSDKsPath) } else { '' }

    $inputList = @()
    # 1. Primary project file
    $inputList += [pscustomobject]@{
        Path = [System.IO.Path]::GetFullPath($ProjectFile)
        Kind = 'project'
    }

    # 2. Known MSBuild implicit and explicit import properties
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
                        return @{
                            Success = $false
                            Reason  = ("Imported file does not exist: '{0}'" -f $impFull)
                        }
                    }
                    $isSdk = $false
                    if (-not [string]::IsNullOrWhiteSpace($netCoreRoot) -and $impFull.StartsWith($netCoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                    if (-not [string]::IsNullOrWhiteSpace($msbuildExtPath) -and $impFull.StartsWith($msbuildExtPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }
                    if (-not [string]::IsNullOrWhiteSpace($msbuildSdksPath) -and $impFull.StartsWith($msbuildSdksPath, [System.StringComparison]::OrdinalIgnoreCase)) { $isSdk = $true }

                    if (-not $isSdk) {
                        $inputList += [pscustomobject]@{
                            Path = $impFull
                            Kind = 'import'
                        }
                    }
                }
            }
        }
    }

    # 3. Comprehensive import extraction via dotnet msbuild -preprocess
    $tempPreprocess = [System.IO.Path]::GetTempFileName()
    try {
        $prepOut = & $DotnetCmd msbuild "-preprocess:$tempPreprocess" $ProjectFile 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tempPreprocess -PathType Leaf)) {
            $prepContent = [System.IO.File]::ReadAllText($tempPreprocess)
            # MSBuild preprocess comments delimit imports with:
            #   <path>
            #   ============================================================================================================================================
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

                    $isProject = ($candFull -eq [System.IO.Path]::GetFullPath($ProjectFile))

                    if (-not $isSdk -and -not $isObjOrBin -and -not $isProject) {
                        if (-not (Test-Path -LiteralPath $candFull -PathType Leaf)) {
                            return @{
                                Success = $false
                                Reason  = ("Imported file discovered via preprocess does not exist: '{0}'" -f $candFull)
                            }
                        }
                        $inputList += [pscustomobject]@{
                            Path = $candFull
                            Kind = 'import'
                        }
                    }
                }
            }
        } else {
            return @{
                Success = $false
                Reason  = ("dotnet msbuild -preprocess failed with code {0}: {1}" -f $LASTEXITCODE, $prepOut)
            }
        }
    } finally {
        if (Test-Path -LiteralPath $tempPreprocess) {
            [System.IO.File]::Delete($tempPreprocess)
        }
    }

    # 4. Source code files (Compile items)
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
                return @{
                    Success = $false
                    Reason  = ("Compile item does not exist: '{0}'" -f $cFull)
                }
            }
            $inputList += [pscustomobject]@{
                Path = $cFull
                Kind = 'compile'
            }
        }
    } else {
        return @{
            Success = $false
            Reason  = "No Compile items found in project"
        }
    }

    # 5. Assembly reference files (Reference items)
    if ($null -ne $mb.Items -and $null -ne $mb.Items.Reference) {
        foreach ($r in $mb.Items.Reference) {
            $h = [string]$r.HintPath
            if (-not [string]::IsNullOrWhiteSpace($h)) {
                if (-not [System.IO.Path]::IsPathRooted($h)) {
                    $h = [System.IO.Path]::Combine($projDir, $h)
                }
                $hFull = [System.IO.Path]::GetFullPath($h)
                if (-not (Test-Path -LiteralPath $hFull -PathType Leaf)) {
                    return @{
                        Success = $false
                        Reason  = ("Reference HintPath does not exist: '{0}'" -f $hFull)
                    }
                }
                $inputList += [pscustomobject]@{
                    Path = $hFull
                    Kind = 'reference'
                }
            } else {
                return @{
                    Success = $false
                    Reason  = ("Reference '{0}' is missing HintPath; non-SDK references must provide explicit paths." -f [string]$r.Identity)
                }
            }
        }
    }

    # Deduplicate input paths case-insensitively while preserving kind
    $dict = [System.Collections.Generic.Dictionary[string, pscustomobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $inputList) {
        if (-not $dict.ContainsKey($item.Path)) {
            $dict.Add($item.Path, $item)
        }
    }

    $sortedPaths = @($dict.Keys) | Sort-Object -CaseSensitive:$false
    $sampled = @()
    foreach ($p in $sortedPaths) {
        $hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
        $sampled += [ordered]@{
            path   = $p
            sha256 = $hash
            kind   = $dict[$p].Kind
        }
    }

    return @{
        Success            = $true
        TargetFramework    = $tfm
        AssemblyName       = $asm
        Configuration      = $cfg
        OutputPathRaw      = $outDirRaw
        EvaluationContract = [ordered]@{
            contract_version           = '3.0'
            supported_target_framework = 'net9.0'
            supported_configurations   = @('Debug', 'Release')
            reject_package_references  = $true
            reject_project_references  = $true
            reject_multi_targeting     = $true
            limitations                = 'Covers evaluation-time Compile, Reference HintPaths, project and imported props/targets outside SDK and intermediate obj/bin folders. Does not support PackageReference, ProjectReference, multi-targeting, or arbitrary MSBuild custom target side-effects outside evaluation.'
        }
        ConfigProperties   = [ordered]@{
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
        Inputs             = $sampled
    }
}

# ---------------------------------------------------------------------------
# Stage 1: validate inputs
# ---------------------------------------------------------------------------
$resolvedProject = $null
if ([string]::IsNullOrWhiteSpace($Project)) {
    $candidates = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
    if ($candidates.Count -ne 1) {
        Stop-Build -Code 1 -Status 'input' -Reason ("expected exactly one .csproj in '{0}', found {1}" -f $projectRoot, $candidates.Count) -Artifact $null
    }
    $resolvedProject = $candidates[0].FullName
} else {
    if ([System.IO.Path]::IsPathRooted($Project)) {
        $resolvedProject = $Project
    } else {
        $resolvedProject = Join-Path (Get-Location).Path $Project
    }
}
$resolvedProject = [System.IO.Path]::GetFullPath($resolvedProject)
$RptProject = $resolvedProject

if (-not (Test-Path -LiteralPath $resolvedProject -PathType Leaf)) {
    Stop-Build -Code 1 -Status 'input' -Reason ("project file not found: '{0}'" -f $resolvedProject) -Artifact $null
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Stop-Build -Code 1 -Status 'input' -Reason 'dotnet was not found on PATH; this script never downloads tooling.' -Artifact $null
}
$RptSdk = (& $dotnet.Source --version | Out-String).Trim()

if (-not (Test-Path -LiteralPath $logPath)) {
    # touched below by the build capture; creating it early keeps the report honest
    $null = New-Item -ItemType File -Path $logPath
}
$RptLogPath = $logPath

# Sample inputs before compilation
$preBuild = Get-BuildInputs -ProjectFile $resolvedProject -DotnetCmd $dotnet.Source
if (-not $preBuild.Success) {
    Stop-Build -Code 1 -Status 'input' -Reason $preBuild.Reason -Artifact $null
}

$RptAssemblyName = $preBuild.AssemblyName
$RptTargetFramework = $preBuild.TargetFramework
$RptConfiguration = $preBuild.Configuration
$RptContract = $preBuild.EvaluationContract
$RptConfigProps = $preBuild.ConfigProperties
$RptInputs = $preBuild.Inputs
$outDirRaw = $preBuild.OutputPathRaw

Write-Output ("project      : {0}" -f $resolvedProject)
Write-Output ("logs         : {0}" -f $LogsDir)
Write-Output ("dotnet sdk   : {0}" -f $RptSdk)

# ---------------------------------------------------------------------------
# Stage 2: build (the command frozen by A03/A05, forced to really compile)
# ---------------------------------------------------------------------------
$buildArgs = @('build', '--no-restore', '-t:Rebuild', '--nologo', $resolvedProject)
$RptCommand = 'dotnet ' + ($buildArgs -join ' ')
Write-Output ("command      : {0}" -f $RptCommand)

# MSBuild writes diagnostics to both streams; merge so the counts see everything.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$buildOutput = & $dotnet.Source @buildArgs 2>&1 | Out-String
$RptBuildExit = $LASTEXITCODE
$ErrorActionPreference = $previousEap

Write-Utf8NoBom -Path $logPath -Text $buildOutput

# Culture-independent diagnostic shape: "file(line,col): error CODE: message".
# The trailing summary line IS localized on this machine ("0 个错误"), so it is not used.
$diag = [regex]::Matches($buildOutput, ':\s*(error|warning)\s+[A-Za-z]+[0-9]*\s*:')
$RptErrors = @($diag | Where-Object { $_.Groups[1].Value -eq 'error' }).Count
$RptWarnings = @($diag | Where-Object { $_.Groups[1].Value -eq 'warning' }).Count
Write-Output ("build exit   : {0}   errors={1} warnings={2}" -f $RptBuildExit, $RptErrors, $RptWarnings)

if ($RptBuildExit -ne 0) {
    Stop-Build -Code 2 -Status 'build-failed' -Reason ("dotnet build exited with {0}; see '{1}'" -f $RptBuildExit, $logPath) -Artifact $null
}

# ---------------------------------------------------------------------------
# Stage 3: verify input stability, locate artifact, and prove freshness
# and hygiene. No bin/<config>/<tfm> guessing.
# ---------------------------------------------------------------------------
$postBuild = Get-BuildInputs -ProjectFile $resolvedProject -DotnetCmd $dotnet.Source
if (-not $postBuild.Success) {
    Stop-Build -Code 3 -Status 'input-drift' -Reason ("Inputs failed to re-sample after build: {0}" -f $postBuild.Reason) -Artifact $null
}

if ($preBuild.Inputs.Count -ne $postBuild.Inputs.Count) {
    Stop-Build -Code 3 -Status 'input-drift' -Reason ("Input count changed during build (before={0}, after={1})" -f $preBuild.Inputs.Count, $postBuild.Inputs.Count) -Artifact $null
}

for ($i = 0; $i -lt $preBuild.Inputs.Count; $i++) {
    $before = $preBuild.Inputs[$i]
    $after = $postBuild.Inputs[$i]
    if ($before.path -ne $after.path -or $before.sha256 -ne $after.sha256 -or $before.kind -ne $after.kind) {
        Stop-Build -Code 3 -Status 'input-drift' -Reason ("Input '{0}' changed during build" -f $before.path) -Artifact $null
    }
}

# Verify configuration stability across build
foreach ($key in $preBuild.ConfigProperties.Keys) {
    $valBefore = [string]$preBuild.ConfigProperties[$key]
    $valAfter = [string]$postBuild.ConfigProperties[$key]
    if ($valBefore -ne $valAfter) {
        Stop-Build -Code 3 -Status 'config-drift' -Reason ("Configuration property '{0}' drifted during build (before='{1}', after='{2}')" -f $key, $valBefore, $valAfter) -Artifact $null
    }
}

$RptInputs = $postBuild.Inputs
$RptConfigProps = $postBuild.ConfigProperties
$asm = $RptAssemblyName

$projectDir = Split-Path -Parent $resolvedProject
$outFull = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($projectDir, ($outDirRaw -replace '/', '\')))
$dllPath = [System.IO.Path]::Combine($outFull, ($asm + '.dll'))

$artifact = Get-Artifact -Path $dllPath
if ($null -eq $artifact) {
    Stop-Build -Code 3 -Status 'artifact-missing' -Reason ("build exited 0 but '{0}' does not exist" -f $dllPath) -Artifact $null
}

# Freshness: this run must have written the dll. -t:Rebuild guarantees it; if the
# timestamp is older than the run, something skipped the compile and the bytes we are
# about to certify are leftovers.
$dllWritten = [DateTime]::Parse($artifact.last_write)
if ($dllWritten -lt $startedAt.AddSeconds(-2)) {
    Stop-Build -Code 3 -Status 'artifact-stale' -Reason ("dll last_write {0} predates this run ({1})" -f $artifact.last_write, $startedAt.ToString('o')) -Artifact $artifact
}

# Hygiene: the output folder may contain only files derived from our own assembly
# (STS2Portrait.dll / .pdb / .deps.json). Anything else - a game, Godot, Harmony or
# RitsuLib copy - is a violation, because the game already loads those and shipping a
# second copy is the documented failure mode (reports/A05.md).
# An allow-list keyed to $asm is used rather than a deny-list of names: a deny-list such
# as '^sts2' also matches 'STS2Portrait.*' under PowerShell's case-INsensitive -match.
# The file list is recorded BEFORE the check so that a failing report still shows what
# was actually in the folder.
$names = @(Get-ChildItem -LiteralPath $outFull -File | ForEach-Object { $_.Name })
$artifact['files'] = $names

$foreign = @(Get-ChildItem -LiteralPath $outFull -File | Where-Object {
    -not $_.Name.StartsWith(($asm + '.'), [System.StringComparison]::OrdinalIgnoreCase)
})
if ($foreign.Count -gt 0) {
    Stop-Build -Code 4 -Status 'output-hygiene' -Reason ("unexpected files in output folder: {0}" -f (($foreign | ForEach-Object { $_.Name }) -join ', ')) -Artifact $artifact
}

Write-Report -Status 'success' -Reason $null -Artifact $artifact
Write-Output ("build exit   : 0")
Write-Output ("dll          : {0}" -f $artifact.path)
Write-Output ("dll bytes    : {0}" -f $artifact.bytes)
Write-Output ("dll sha256   : {0}" -f $artifact.sha256)
Write-Output ("dll lastwrite: {0}" -f $artifact.last_write)
Write-Output ("output files : {0}" -f ($names -join ', '))
Write-Output 'BUILD OK (compile only - nothing was deployed or launched by this script)'
exit 0
