# Adapt private build copies only; ChatCraft's shared source and policies stay independent.
param([string]$App,[string]$Stage)
$ErrorActionPreference='Stop'
foreach($name in 'GpuPresenter.cs','GpuSumiInk.cs','WordViewEffects.cs'){
 $source=[IO.File]::ReadAllText((Join-Path $App $name)).Replace('SumiInkGpu.dll','WordCraftSmoke.dll').Replace('View-Quality.txt','WordCraft-Smoke-Quality.txt')
 if($name -eq 'GpuPresenter.cs'){
  $needle='int chosen = (int)Math.Round(ViewTuning.Get(prefix + "iterations"));'
  if(!$source.Contains($needle)){throw 'Review quality pressure staging.'}
  $source=$source.Replace($needle,'int chosen = WordCraftQuality.Iterations;')
 }elseif($name -eq 'GpuSumiInk.cs'){
  foreach($pair in @(@('GpuQuality.Rows(tier)','WordCraftQuality.DyeRows'),@('flowHeight = simulationRows;','flowHeight = WordCraftQuality.FlowRows;'),@('Math.Round(simulationRows * aspect)','Math.Round(WordCraftQuality.FlowRows * aspect)'),@('GpuQuality.Iterations(tier)','WordCraftQuality.Iterations'))){
   if(!$source.Contains($pair[0])){throw 'Review quality grid staging.'};$source=$source.Replace($pair[0],$pair[1])
  }
 }else{
  $source=$source.Replace('if (GpuQuality.Observe(milliseconds))','if (WordCraftQuality.Observe(milliseconds))')
  $source=$source.Replace('Math.Max(minimumInkFrameMilliseconds,','Math.Max(Math.Max(minimumInkFrameMilliseconds,WordCraftQuality.FrameMilliseconds),')
  $source=$source.Replace('Math.Max(16, minimumInkFrameMilliseconds)','Math.Max(16, Math.Max(minimumInkFrameMilliseconds,WordCraftQuality.FrameMilliseconds))')
  $source=$source.Replace('void UpdateTimer()','internal void RefreshQuality(){timer.Interval=TimeSpan.FromMilliseconds(WordCraftQuality.FrameMilliseconds);if(started)ApplyViewport();UpdateTimer();redraw();}'+"`n"+'    void UpdateTimer()')
  $source=$source.Replace('if (InkActive && timer.Interval.TotalMilliseconds < minimumInkFrameMilliseconds)','if (InkActive && timer.Interval.TotalMilliseconds < Math.Max(minimumInkFrameMilliseconds,WordCraftQuality.FrameMilliseconds))').Replace('TimeSpan.FromMilliseconds(minimumInkFrameMilliseconds);','TimeSpan.FromMilliseconds(Math.Max(minimumInkFrameMilliseconds,WordCraftQuality.FrameMilliseconds));')
 }
 [IO.File]::WriteAllText((Join-Path $Stage $name),$source)
}
