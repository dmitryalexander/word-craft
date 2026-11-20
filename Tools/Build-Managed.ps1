$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=Split-Path $project -Parent
$app=Join-Path $project 'Bleeding-Edge/App'
$page=$app
$chat=$app
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$assemblies=Join-Path $env:WINDIR 'Microsoft.NET/assembly'
$references=foreach($name in 'PresentationCore','PresentationFramework','WindowsBase','System.Xaml','WindowsFormsIntegration'){
 $architecture=if($name -eq 'PresentationCore'){'GAC_64'}else{'GAC_MSIL'}
 $reference=Get-ChildItem (Join-Path $assemblies "$architecture/$name") -Filter "$name.dll" -Recurse|Select-Object -First 1
 if(!$reference){throw "Missing framework reference: $name"}
 "/reference:$($reference.FullName)"
}
$sources=@('WordCraftEditor.cs','WordCraftQuality.cs','WordCraftWindSurface.cs','WordCraftClothSimulation.cs','WordCraftCanvasMaterial.cs','WordCraftCharacterCaret.cs','WordCraftPhaseColors.cs','WordCraftSettings.cs','WordCraftAudio.cs','WordFlowView.cs','FlowMarkdown.cs','WordViewEffects.cs','ViewTuning.cs','SmokeLoadingWorkshop.cs')|ForEach-Object{Join-Path $app $_}
$sources+=@('KineticReader.cs','FlowTuningPanel.cs','ReaderRaster.cs','RuledReader.cs','ReaderNarration.cs','NarrationEffects.cs')|ForEach-Object{Join-Path $page $_}
$sources+=@('JapaneseRomaji.cs','FlowSmokeOverlay.cs','ChatTypography.cs','ChatColorPanel.cs')|ForEach-Object{Join-Path $chat $_}
$stage=New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('WordCraft-reader-'+[guid]::NewGuid()))
$exe=Join-Path $app 'WordCraft.exe'
$icon=Join-Path $app 'WordCraft.ico'
try {
 # Local partial companion smooths WordCraft phase colors without changing ChatCraft.
 $source=[IO.File]::ReadAllText((Join-Path $chat 'CloudSmokeView.cs'))
 foreach($pair in @(
  @('sealed class CloudSmokeView','sealed partial class CloudSmokeView'),
  @('void ApplyColors()','void ApplyBaseColors()'),
  @('void DrawBackground(DrawingContext drawing)','void DrawBaseBackground(DrawingContext drawing)'),
  @('Colors.Changed += () => { ApplyColors();','Colors.Changed += () => { ApplyColors(true);'),
  @('Color corner=Colors.Get(previewPhase ?? Phase,"corner");','Color corner=Colors.Get(appliedColorPhase<0?(previewPhase??Phase):appliedColorPhase,"corner");'),
  @('Phase==0 ? Colors.Strength(0) : 0','phase==0 ? Colors.Strength(0) : 0')
 )){if(!$source.Contains($pair[0])){throw 'Shared color scene changed; review WordCraft phase staging.'};$source=$source.Replace($pair[0],$pair[1])}
 $source=$source.Replace("`r`n","`n")
 $needle="void DrawBaseBackground(DrawingContext drawing)`n    {`n        int phase=previewPhase ?? Phase;"
 if(!$source.Contains($needle)){throw 'Missing background phase hook.'}
 $source=$source.Replace($needle,"void DrawBaseBackground(DrawingContext drawing)`n    {`n        int phase=appliedColorPhase<0?(previewPhase??Phase):appliedColorPhase;")
 $needle="void SetReplyPalette(bool enabled)`n    {"
 if(!$source.Contains($needle)){throw 'Missing reading color exit guard hook.'}
 $source=$source.Replace($needle,$needle+"`n        if(readingColorExit && !enabled && !replyPalette)return;")
 $destination=Join-Path $stage.FullName 'CloudSmokeView.cs';[IO.File]::WriteAllText($destination,$source);$sources+=$destination
 # WordCraft-only continuous-noise callback; original shared Morse source stays unchanged.
 $source=[IO.File]::ReadAllText((Join-Path $chat 'DialUpMorse.cs'))
 $changes=@(
  @('internal MorseOutput(bool enableOutput = true)','internal MorseOutput(bool enableOutput = true, Action<short[]> generator = null)'),
  @('readonly Thread pump;','readonly Thread pump; readonly Action<short[]> generator;'),
  @('if (!enableOutput) return;','this.generator=generator; if (!enableOutput) return;'),
  @('int Fill(short[] destination)','int Fill(short[] destination)')
 )
 foreach($pair in $changes){if(!$source.Contains($pair[0])){throw 'Shared Morse source changed; review WordCraft transport staging.'};$source=$source.Replace($pair[0],$pair[1])}
 $needle="int Fill(short[] destination)`n    {"
 $source=$source.Replace("`r`n","`n")
 if(!$source.Contains($needle)){throw 'Missing native audio fill hook.'}
 $source=$source.Replace($needle,$needle+"`n        if(generator!=null){generator(destination);return destination.Length;}")
 $destination=Join-Path $stage.FullName 'DialUpMorse.cs';[IO.File]::WriteAllText($destination,$source);$sources+=$destination
 # Give this app its own native module/profile names; legacy CodexCraft keeps its DLL and settings.
 foreach($name in 'GpuPresenter.cs','GpuSumiInk.cs','GpuWater.cs','GpuFlow.cs'){
  $source=[IO.File]::ReadAllText((Join-Path $app $name)).Replace('SumiInkGpu.dll','WordCraftSmoke.dll').Replace('View-Quality.txt','WordCraft-Smoke-Quality.txt')
  $destination=Join-Path $stage.FullName $name;[IO.File]::WriteAllText($destination,$source);$sources+=$destination
 }
 $sources=@($sources|Where-Object{[IO.Path]::GetFileName($_) -ne 'ViewTuning.cs'})
 $source=[IO.File]::ReadAllText((Join-Path $app 'ViewTuning.cs')).Replace('View-Tuning.txt','WordCraft-Smoke-Tuning.txt')
 $destination=Join-Path $stage.FullName 'ViewTuning.cs';[IO.File]::WriteAllText($destination,$source);$sources+=$destination
 & (Join-Path $PSScriptRoot 'Stage-WordCraftQuality.ps1') -App $app -Stage $stage.FullName
 $sources=@($sources|Where-Object{[IO.Path]::GetFileName($_) -ne 'WordViewEffects.cs'})
 $sources+=(Join-Path $stage.FullName 'WordViewEffects.cs')
 $built=Join-Path $stage.FullName 'WordCraft.exe'
 & $compiler /nologo /target:winexe /platform:x64 /optimize+ /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll @references "/win32icon:$icon" "/resource:$icon,WordCraft.ico" "/resource:$(Join-Path $chat 'DialUpMorse.pcm'),DialUpMorse.pcm" "/out:$built" @sources
 if($LASTEXITCODE){throw 'WordCraft smoke reader/writer compilation failed.'}
 if(Test-Path $exe){throw 'Output exists; preserve it before rebuilding.'}
 Copy-Item -LiteralPath $built -Destination $exe
 foreach($file in Get-ChildItem (Join-Path $project 'Defaults') -File){
  $destination=Join-Path $project ('Bleeding-Edge/'+$file.Name)
  if(!(Test-Path $destination)){Copy-Item -LiteralPath $file.FullName -Destination $destination}
 }
 New-Item -ItemType Directory -Force -Path (Join-Path $project 'Bleeding-Edge/Validation')|Out-Null
 Write-Output "Built smoke reader/writer: $exe"
}finally {
 Get-ChildItem -LiteralPath $stage.FullName -File|Remove-Item
 Remove-Item -LiteralPath $stage.FullName
}
