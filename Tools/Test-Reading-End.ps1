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
 $grid.UpdateLayout();$editor=Field $window editor;$scene=Field $window scene
 $editor.Text='What is the meaning of life?';$original=$editor.Text
 Call $window ShowReader @($true,$true)
 $reader=Prop (Field $window flow) Reader;$view=Field $window readingView
 $reader.Seek($original.Length);$reader.StartPlayback(1)
 [void]$reader.AdvancePlayback(1)
 Check ($reader.PlaybackDirection -eq 0 -and (Prop $reader AtBufferedEnd)) 'Fixture did not reach natural end'
 Call $scene ClearReplySmoke
 (Field $reader timer).Start()
 Check ($reader.Animating) 'Fixture lacks remaining reader motion'
 $view.BeginAnimation([Windows.UIElement]::OpacityProperty,$null);$view.Opacity=1
 Check (Call $window TryFinishReading) 'Natural completion waited for reader motion'
 Check (!(Field $window reading) -and $view.Visibility -eq 'Visible' -and !$view.IsHitTestVisible) 'Completion did not begin fade to typing'
 $coast=Field $window readingCoast
 Check ($coast.HasAnimatedProperties) 'End fade does not keep scrolling'
 PumpFor 90;$firstX=$coast.X
 PumpFor 90;$secondX=$coast.X
 Check ($firstX -lt 0 -and $secondX -lt $firstX) 'Ending text stopped or moved backwards during fade'
 Draw "WordCraft-Scrolling-end-$version.png"
 PumpFor 820
 Check ($view.Visibility -eq 'Collapsed') 'Natural end did not finish the matched mode fade'
 Check ($editor.Text -eq $original) 'Completion changed source'
 Call $window ShowReader @($true,$true)
 $reader.Seek($original.Length)
 Call $window ToggleReadingPlayback
 Call $scene ClearReplySmoke
 Check (!(Call $window TryFinishReading) -and (Field $window reading)) 'Manual pause on final word was treated as completion'
 Call $window ShowReader @($true,$true)
 $reader.Seek($original.Length);$reader.StartPlayback(1);[void]$reader.AdvancePlayback(1)
 Call $scene ClearReplySmoke
 [void](Call $window TryFinishReading)
 Call $window ShowReader @($false,$true)
 Check ($coast.X -eq 0 -and !$coast.HasAnimatedProperties) 'Replay retained the exit scroll offset'
 PumpFor 820
 Check ((Field $window reading) -and $view.Visibility -eq 'Visible') 'Old end fade hid restarted reading'
 $reader.PausePlayback();(Field $window timer).Stop()
 'PASS: ending keeps moving left during matched fade; manual final-word pause stays visible; replay resets coast/cancels old fade; source intact. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
