$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function PumpFor([int]$milliseconds){
 $frame=New-Object Windows.Threading.DispatcherFrame;$wake=New-Object Windows.Threading.DispatcherTimer
 $wake.Interval=[TimeSpan]::FromMilliseconds($milliseconds)
 $wake.Add_Tick({$wake.Stop();$frame.Continue=$false}.GetNewClosure());$wake.Start()
 [Windows.Threading.Dispatcher]::PushFrame($frame)
}
try {
 $grid.UpdateLayout();$editor=Field $window editor;$scene=Field $window scene;$profiles=Field $scene Colors
 $colorPath=Field $profiles FilePath;$before=(Get-FileHash $colorPath).Hash
 Call $profiles Set @(0,'background',[Windows.Media.Color]::FromRgb(10,15,20))
 Call $profiles Set @(0,'corner',[Windows.Media.Color]::FromRgb(20,25,30))
 Call $profiles Set @(2,'background',[Windows.Media.Color]::FromRgb(80,50,110))
 Call $profiles Set @(2,'corner',[Windows.Media.Color]::FromRgb(100,65,130))
 $editor.Text='Finish at the last letter.';$source=$editor.Text
 Call $window ShowReader @($true,$true)
 $reader=Prop (Field $window flow) Reader;$reader.SetSpeed(1)
 PumpFor 820
 $reader.Seek($source.Length);$reader.StartPlayback(1);[void]$reader.AdvancePlayback(1)
 (Field $window timer).Stop()
 Check ((Field $scene readingColorExit) -and (Field $scene appliedColorPhase) -eq 0) 'Colors did not begin exiting directly at source exhaustion'
 Check ((Prop $scene PhaseColorProgress) -eq 0) 'Color exit first frame jumped to the target'
 Check ((Field $window reading)) 'Test did not capture color exit before text-mode completion'
 Call $scene QueueReplyLetter @(0,'last','t')
 Check (!(Call $window TryFinishReading)) 'Pending-letter fixture unexpectedly ended text mode'
 $ramp=Field $scene previousColorRamp
 PumpFor 150
 $progress=Prop $scene PhaseColorProgress
 Check ($progress -gt 0 -and $progress -lt 1) 'Color fade jumped instead of interpolating'
 $smoke=Field $scene smoke;$gradient=Field $smoke gradientStart
 Check ($gradient.R -gt 10 -and $gradient.R -lt 80) 'GPU smoke gradient did not interpolate'
 Check ([Object]::ReferenceEquals($ramp,(Field $scene previousColorRamp))) 'Background ramp regenerated every frame'
 Draw "WordCraft-Ending-colors-mid-$version.png"
 Call $scene ClearReplySmoke
 Check (Call $window TryFinishReading) 'Text mode did not complete'
 Check ((Prop $scene PhaseColorProgress) -ge $progress) 'Typing return restarted the already-running color fade'
 PumpFor 820
 Check ((Prop $scene PhaseColorProgress) -eq 1 -and (Field $smoke gradientStart).R -eq 10) 'Color exit did not reach typing palette'
 Draw "WordCraft-Ending-colors-end-$version.png"
 Call $window ShowReader @($true,$true)
 $reader.SetSpeed(1);PumpFor 120
 Call $scene BeginReadingColorExit;PumpFor 120
 $current=Field $smoke gradientStart
 Call $scene ResetReadingColorExit;Call $scene BeginReply
 $restarted=Field $smoke gradientStart
 Check ([Math]::Abs($current.R-$restarted.R) -le 1) 'Interrupted color fade jumped on reading reentry'
 Check ((Field $scene appliedColorPhase) -eq 2) 'Reentry kept typing palette target'
 $reader.PausePlayback();(Field $window timer).Stop()
 Check ($editor.Text -eq $source -and (Get-FileHash $colorPath).Hash -eq $before) 'Color transition changed source or saved palette'
 'PASS: immediate end-event color exit, smooth background/GPU gradient progress, cached ramps, no exit restart, continuous interrupted reentry, unchanged source/preferences. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
