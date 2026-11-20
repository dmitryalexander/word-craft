$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function SetProp($o,$name,$value){$o.GetType().GetProperty($name,$flags).SetValue($o,$value,$null)}
function PumpFor([int]$milliseconds) {
 $frame=New-Object Windows.Threading.DispatcherFrame
 $wake=New-Object Windows.Threading.DispatcherTimer
 $wake.Interval=[TimeSpan]::FromMilliseconds($milliseconds)
 $wake.Add_Tick({$wake.Stop();$frame.Continue=$false}.GetNewClosure());$wake.Start()
 [Windows.Threading.Dispatcher]::PushFrame($frame)
}
function Samples($generator,[int]$count){
 $buffer=[Array]::CreateInstance([Int16],$count)
 $arguments=New-Object 'System.Object[]' 1;$arguments[0]=$buffer.PSObject.BaseObject
 [void]$generator.GetType().GetMethod('Fill',$flags).Invoke($generator,$arguments)
 return ,$buffer
}
function Energy($samples,[int]$start,[int]$count){$sum=0.;for($i=$start;$i -lt $start+$count;$i++){$sum+=[double]$samples[$i]*$samples[$i]};return [Math]::Sqrt($sum/$count)}
try {
 $grid.UpdateLayout();$editor=Field $window editor;$sound=Field $window sound
 Check ($null -eq $assembly.GetType('WordCueSound')) 'Retired experimental wrapper remains compiled'
 $generator=Field $sound noise
 SetProp $generator PlaybackOnly $true;SetProp $generator Playing $false
 SetProp $generator SecondaryWhileReading $false;SetProp $generator Kind 0
 SetProp $generator Level ([double]1);SetProp $generator Master ([double]1)
 $idle=Samples $generator 2400
 Check ((Energy $idle 0 2400) -eq 0) 'Playback-only noise runs while idle'
 $ratios=@()
 for($kind=0;$kind -lt 3;$kind++){
  SetProp $generator Kind $kind;SetProp $generator Playing $true
  $buffer=Samples $generator 48000
  $energy=Energy $buffer 36000 12000;$difference=0.
  for($i=36001;$i -lt 48000;$i++){$delta=[double]$buffer[$i]-$buffer[$i-1];$difference+=$delta*$delta}
  Check ($energy -gt 100) 'Noise mode is silent'
  $ratios+=([Math]::Sqrt($difference/11999)/$energy)
 }
 Check ($ratios[1] -lt $ratios[0]*0.85 -and $ratios[2] -lt $ratios[1]*0.85) 'Dark/darker/darkest spectra are not progressively darker'
 SetProp $generator Kind 0;SetProp $generator SecondaryKind 1;SetProp $generator SecondaryLevel ([double]0.5)
 SetProp $generator SecondaryWhileReading $true;SetProp $generator PlaybackOnly $false
 SetProp $generator Playing $false
 [void](Samples $generator 48000)
 Check ((Field $generator primaryAmplitude) -gt .99 -and (Field $generator secondaryAmplitude) -eq 0) 'Idle does not retain just primary'
 SetProp $generator Playing $true
 [void](Samples $generator 2400)
 Check ((Field $generator primaryAmplitude) -gt .99 -and (Field $generator secondaryAmplitude) -gt .1 -and (Field $generator secondaryAmplitude) -lt .5) 'Reading replaced primary or did not fade in secondary'
 [void](Samples $generator 48000)
 Check ((Field $generator primaryAmplitude) -gt .99 -and (Field $generator secondaryAmplitude) -gt .49) 'Reading does not retain both layers'
 SetProp $generator Playing $false
 [void](Samples $generator 2400)
 Check ((Field $generator primaryAmplitude) -gt .99 -and (Field $generator secondaryAmplitude) -gt .1 -and (Field $generator secondaryAmplitude) -lt .5) 'Stopping changed primary or did not fade added layer'
 [void](Samples $generator 168000)
 Check ((Field $generator primaryAmplitude) -gt .99 -and (Field $generator secondaryAmplitude) -eq 0) 'Added layer does not fade fully out'
 # Compare identical deterministic stream states: combined PCM equals primary+secondary.
 $parts=@()
 foreach($pair in @(@(1.,0.),@(0.,1.),@(1.,1.))){
  $g=[Activator]::CreateInstance($assembly.GetType('NoiseGenerator'),$true)
  SetProp $g Level ([double]$pair[0]);SetProp $g SecondaryLevel ([double]$pair[1])
  SetProp $g SecondaryWhileReading $true;SetProp $g Playing $true
  $parts+=,(Samples $g 24000)
 }
 for($i=0;$i -lt 24000;$i++){
  Check ([Math]::Abs([int]$parts[2][$i]-[int]$parts[0][$i]-[int]$parts[1][$i]) -le 1) 'Secondary mix attenuates/replaces primary instead of adding'
 }
 SetProp $generator SecondaryWhileReading $false;SetProp $generator PlaybackOnly $true;SetProp $generator Playing $true
 [void](Samples $generator 48000)
 SetProp $generator Playing $false
 $fade=Samples $generator 48000
 Check ((Energy $fade 45600 2400) -lt (Energy $fade 0 2400)*0.03) 'Noise did not fade after playback stopped'
 [void](Samples $generator 120000)
 Check ((Energy (Samples $generator 2400) 0 2400) -eq 0) 'Faded noise never reaches silence'
 SetProp $generator PlaybackOnly $false
 Check ((Energy (Samples $generator 24000) 21600 2400) -gt 100) 'Continuous toggle does not run idle noise'
 SetProp $generator Suspended $true
 [void](Samples $generator 168000)
 Check ((Energy (Samples $generator 2400) 0 2400) -eq 0) 'Minimize does not silence noise'
 SetProp $generator Suspended $false;SetProp $generator PlaybackOnly $true
 $editor.Text='Return smoothly to typing without changing these words.'
 $original=$editor.Text
 Call $window ShowFullEditor;$editor.SelectAll()
 $handled=Call $window HandleTypingShortcut @([Windows.Input.Key]::A,[Windows.Input.ModifierKeys]::Alt,$false)
 Check ($handled -and !(Field $window reading) -and !$editor.IsHitTestVisible -and $editor.SelectionLength -eq 0) 'Alt+A did not return to typing/deselect full text'
 Check ($editor.Text -eq $original -and $editor.HasAnimatedProperties) 'Full-text exit lacks transition or changed source'
 PumpFor 820
 Check ($editor.Opacity -eq 0) 'Full-text return fade did not finish'
 Call $window ShowReader @($true,$true)
 $reader=Prop (Field $window flow) Reader;$reader.SetSpeed(1)
 Check ((Prop $generator Playing)) 'Reading did not turn on playback-gated noise'
 $reader.PausePlayback()
 Check (!(Prop $generator Playing)) 'Pause did not gate noise off'
 $view=Field $window readingView
 $view.BeginAnimation([Windows.UIElement]::OpacityProperty,$null);$view.Opacity=1
 Call $window ReturnToTyping
 Check ($view.Visibility -eq 'Visible' -and !$view.IsHitTestVisible -and $view.HasAnimatedProperties) 'Reading exit snapped or remains interactive'
 PumpFor 80
 Check ($view.Opacity -gt 0 -and $view.Opacity -lt 1) 'Reading exit has no intermediate fade'
 Draw "WordCraft-Typing-transition-mid-$version.png"
 PumpFor 820
 Check ($view.Visibility -eq 'Collapsed' -and !(Field $window reading)) 'Reading exit fade did not finish'
 Call $window ShowReader @($false,$true)
 $view.BeginAnimation([Windows.UIElement]::OpacityProperty,$null);$view.Opacity=1
 Call $window ReturnToTyping
 Call $window ShowReader @($false,$true)
 PumpFor 820
 Check ((Field $window reading) -and $view.Visibility -eq 'Visible') 'Stale fade completion hid reopened reading'
 Check ($editor.Text -eq $original) 'Transition lost source'
 Draw "WordCraft-Noise-Settings-$version.png"
 'PASS: three distinct noise spectra, playback gate/fade/idle toggle/minimize silence; Alt+A full-text return/deselection/source preservation; reading exit animation/completion and interrupt safety. No audio device or visible window.'
}finally{$hostWindow.Dispose();$window.Close()}
