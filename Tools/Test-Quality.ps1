$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n");$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot');Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function SetProp($o,$name,$value){$o.GetType().GetProperty($name,$flags).SetValue($o,$value,$null)}
try {
 $quality=Field $window quality;$scene=Field $window scene;$smoke=Field $scene smoke;$editor=Field $window editor
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Quality-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 SetField $quality FilePath (Join-Path $fixture 'Quality.json')
 $grid.UpdateLayout();$editor.Text='Quality keeps my text unchanged.';$source=$editor.Text
 Call $window ToggleSettings;$settings=Field $window settings;$slider=Field $settings quality
 $costs=@();$expectedRows=@(144,288,576,576);$expectedDye=@(540,720,1080,2160);$expectedIterations=@(8,12,32,32);$expectedIntervals=@(50,33,16,16)
 foreach($level in 3,0,1,2){
  $slider.Value=$level
  $gpu=Field $smoke gpuInk
  Check ((Prop $quality Level) -eq $level) 'Actual slider did not apply quality'
  Check ((Field $gpu flowHeight) -eq $expectedRows[$level]) 'Quality did not resize native flow grid'
  Check ((Field $gpu height) -eq [Math]::Min($expectedDye[$level],(Field $smoke pixels).Height)) 'Quality did not resize dye output'
  Check ((Field $gpu iterations) -eq $expectedIterations[$level]) 'Pressure work not reduced'
  Call $gpu Advance
  Check (!(Prop $gpu Failed)) 'Quality resize lost GPU'
  Check ((Field $smoke timer).Interval.TotalMilliseconds -ge $expectedIntervals[$level]) 'Quality cadence cap bypassed'
  Check ((Get-Content (Field $quality FilePath) -Raw|ConvertFrom-Json).quality -eq $level) 'Quality was not saved'
  for($i=0;$i -lt 8;$i++){Call $gpu Advance}
  $samples=@();for($i=0;$i -lt 12;$i++){Call $gpu Advance;$samples+=Prop $gpu Milliseconds}
  $costs+= [pscustomobject]@{quality=$level;flowRows=Field $gpu flowHeight;dyeRows=Field $gpu height;iterations=Field $gpu iterations;configuredFrameMilliseconds=$expectedIntervals[$level]}
 }
 Check ($editor.Text -eq $source) 'Quality changed document source'
 $type=$assembly.GetType('WordCraftQuality');$ctor=$type.GetConstructor($flags,$null,[type[]]@([Action],[string]),$null)
 $args=New-Object 'System.Object[]' 2;$args[0]=[Action]{};$args[1]=[string](Field $quality FilePath);$reload=$ctor.Invoke($args)
 Check ((Prop $reload Level) -eq 2) 'Quality failed reload'
 $args[1]=[string](Join-Path $fixture 'Absent.json');$fresh=$ctor.Invoke($args);Check ((Prop $fresh Level) -eq 1) 'Fresh quality is not Balanced'
 [IO.File]::WriteAllText((Join-Path $fixture 'Absent.json'),'invalid');$bad=$ctor.Invoke($args);Check ((Prop $bad Level) -eq 1) 'Malformed settings broke safe default'
  $slider.Value=0;Draw ('WordCraft-Quality-'+$version+'.png')
 Call $scene SetSuspended @($true);SetProp $quality Level 3;Check (!((Field $smoke timer).IsEnabled)) 'Quality restarted suspended smoke'
 $costs|ConvertTo-Json|Set-Content (Join-Path $fixture 'Measurements.json')
 $costs|Format-Table
 'PASS: actual quality slider changes native dye/flow grids, pressure and cadence; settings save/reload/default/error, source preservation and suspended lifecycle. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
