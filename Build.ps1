$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Tools/Build-Native.ps1')
& (Join-Path $PSScriptRoot 'Tools/Build-Managed.ps1')
