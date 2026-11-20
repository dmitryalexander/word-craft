$ErrorActionPreference='Stop'
$app=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Bleeding-Edge/App'))
$NativeOutputPath=Join-Path $app 'WordCraftSmoke.dll'
if(Test-Path $NativeOutputPath){throw 'Output exists; preserve it before rebuilding.'}
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem (Join-Path $kits 'bin') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64/fxc.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$sdk -or !$vs) { throw 'The Windows SDK and Visual Studio x64 C++ Build Tools are required for Sumi Ink.' }
$vc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$include = Join-Path $kits "Include/$($sdk.Name)"
$lib = Join-Path $kits "Lib/$($sdk.Name)"
$stage = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('WordCraft-build-' + [guid]::NewGuid()))
try {
    & (Join-Path $sdk.FullName 'x64/fxc.exe') /nologo /WX /T cs_5_0 /E Fluid /Vn SumiInkBytecode /Fh (Join-Path $stage.FullName 'SumiInkShader.h') (Join-Path $app 'SumiInk.hlsl')
    if ($LASTEXITCODE) { throw 'Sumi Ink shader compilation failed.' }
    foreach ($pass in @(
        @{ Source = 'WaterCaustics.hlsl'; Target = 'vs_5_0'; Entry = 'ProjectLight'; Name = 'WaterLightVertex' },
        @{ Source = 'WaterCaustics.hlsl'; Target = 'ps_5_0'; Entry = 'FocusLight'; Name = 'WaterLightPixel' },
        @{ Source = 'Present.hlsl'; Target = 'vs_5_0'; Entry = 'ShowVertex'; Name = 'PresentVertex' },
        @{ Source = 'Present.hlsl'; Target = 'ps_5_0'; Entry = 'ShowPixel'; Name = 'PresentPixel' },
        @{ Source = 'Particles.hlsl'; Target = 'vs_5_0'; Entry = 'ShowParticle'; Name = 'ParticleVertex' },
        @{ Source = 'Particles.hlsl'; Target = 'ps_5_0'; Entry = 'GlowParticle'; Name = 'ParticlePixel' }
    )) {
        & (Join-Path $sdk.FullName 'x64/fxc.exe') /nologo /WX /T $pass.Target /E $pass.Entry /Vn $pass.Name /Fh (Join-Path $stage.FullName "$($pass.Name).h") (Join-Path $app $pass.Source)
        if ($LASTEXITCODE) { throw "Shader compilation failed for $($pass.Entry) in $($pass.Source)." }
    }
    $native = Join-Path $stage.FullName 'SumiInkGpu.dll'
    $importLibrary = Join-Path $stage.FullName 'SumiInkGpu.lib'
    & (Join-Path $vc.FullName 'bin/Hostx64/x64/cl.exe') /nologo /O2 /EHsc /MT /LD "/I$($stage.FullName)" "/I$($vc.FullName)/include" "/I$include/ucrt" "/I$include/shared" "/I$include/um" "/I$include/winrt" "/Fo$($stage.FullName)/SumiInkGpu.obj" (Join-Path $app 'SumiInkGpu.cpp') /link "/OUT:$native" "/IMPLIB:$importLibrary" "/LIBPATH:$($vc.FullName)/lib/x64" "/LIBPATH:$lib/ucrt/x64" "/LIBPATH:$lib/um/x64" d3d11.lib d3d9.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native GPU module compilation failed.' }
    Copy-Item -LiteralPath $native -Destination $NativeOutputPath
}finally {
 Get-ChildItem -LiteralPath $stage.FullName -File|Remove-Item
 Remove-Item -LiteralPath $stage.FullName
}
