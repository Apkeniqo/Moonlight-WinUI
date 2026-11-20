#Requires -Version 5.1
<#
.SYNOPSIS
    Thin wrapper over build-native.bat - the .bat is the single source of truth
    for compiler flags and inputs (mlrender + mlsession + mlaudio).
    Dependency discovery and the MSVC environment live in native\env.bat.
.PARAMETER Deps
    Optional Moonlight-Qt-with-FluentUI root; exported as ML_DEPS for this run.
#>
param(
    [string]$Deps
)

if ($Deps) { $env:ML_DEPS = $Deps }

& (Join-Path $PSScriptRoot 'build-native.bat')
if ($LASTEXITCODE -ne 0) { throw "build-native.bat failed (exit $LASTEXITCODE)" }
