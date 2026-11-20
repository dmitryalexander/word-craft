$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n");$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot');Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
try {
 $grid.UpdateLayout();$editor=Field $window editor;$editor.Text='CAPS LOCK stays editable while the window changes size.';$source=$editor.Text
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Window-Zoom-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 SetField (Field (Field $window scene) Colors) FilePath (Join-Path $fixture 'Colors.json');SetField $window audioPath (Join-Path $fixture 'Audio.json')
 $type=$window.GetType();$zoomBounds=$type.GetMethod('ZoomBounds',[Reflection.BindingFlags]'Static,NonPublic')
 $negative=New-Object Drawing.Rectangle -3840,-1080,3840,2160
 $quarter=$zoomBounds.Invoke($null,[object[]]@($negative,[double]0.25))
 Check ($quarter.Width -eq 960 -and $quarter.Height -eq 540 -and $quarter.X -eq -2400 -and $quarter.Y -eq -270) 'Negative-origin monitor centering is wrong'
 foreach($fraction in -1,0,.25,.5,1,3){
  $bounds=$zoomBounds.Invoke($null,[object[]]@($negative,[double]$fraction))
  Check ($bounds.Width -ge 960 -and $bounds.Width -le 3840 -and $bounds.Height -ge 540 -and $bounds.Height -le 2160) '25-100 percent bounds escaped'
 }
 Call $window SetWindowZoom @([double]1)
 $handle=(New-Object Windows.Interop.WindowInteropHelper $window).Handle;$monitor=[Windows.Forms.Screen]::FromHandle($handle).Bounds
 Check ((Prop $window WindowPixelBounds) -eq $monitor) 'Fullscreen does not cover monitor physical pixels'
 Check (!(Prop $window IsVisible)) 'Zoom fixture showed a desktop window'
 Check (Call $window HandleWindowZoomWheel @(-120,[Windows.Input.ModifierKeys]::None)) 'Plain wheel not consumed'
 $bounds=Prop $window WindowPixelBounds
 Check ($bounds -eq ($zoomBounds.Invoke($null,[object[]]@($monitor,[double]0.95)))) ("Wheel down is not centered 95 percent: actual="+$bounds+" expected="+($zoomBounds.Invoke($null,[object[]]@($monitor,[double]0.95)))+" monitor="+$monitor)
 Check (Call $window HandleWindowZoomWheel @(120,[Windows.Input.ModifierKeys]::None)) 'Wheel up not consumed'
 Check ((Prop $window WindowPixelBounds) -eq $monitor) 'Wheel up did not return to fullscreen'
 Call $window HandleWindowZoomWheel @(-12000,[Windows.Input.ModifierKeys]::None)|Out-Null
 $minimum=$zoomBounds.Invoke($null,[object[]]@($monitor,[double]0.25))
 Check ((Prop $window WindowPixelBounds) -eq $minimum) 'Wheel did not clamp to 25 percent'
 Call $window HandleWindowZoomWheel @(-120,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ((Prop $window WindowPixelBounds) -eq $minimum) 'Wheel went below minimum'
 Call $window HandleWindowZoomWheel @(30,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ((Prop $window WindowPixelBounds).Width -gt $minimum.Width) 'High-resolution wheel delta was discarded'
 $before=Prop $window WindowPixelBounds
 foreach($modifier in [Windows.Input.ModifierKeys]::Control,[Windows.Input.ModifierKeys]::Shift,[Windows.Input.ModifierKeys]::Alt){Check (!(Call $window HandleWindowZoomWheel @(120,$modifier))) 'Modified wheel was intercepted'}
 Check ((Prop $window WindowPixelBounds) -eq $before) 'Modified wheel resized window'
 Call $window ShowReader @($false,$true);$reader=Prop (Field $window flow) Reader;$reader.Seek(13);$anchor=$reader.TextAnchor
 Call $window HandleWindowZoomWheel @(120,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ($reader.TextAnchor -eq $anchor -and $reader.PlaybackDirection -eq 0 -and $editor.Text -eq $source) 'Window zoom moved reading position, started playback or edited text'
 Call $window ToggleSettings;$before=Prop $window WindowPixelBounds
 Check (!(Call $window HandleWindowZoomWheel @(120,[Windows.Input.ModifierKeys]::None)) -and (Prop $window WindowPixelBounds) -eq $before) 'Options wheel was stolen'
 Call $window CloseSettings
 Check (!(Prop $window IsVisible)) 'Hidden test window became visible'
 'PASS: real hidden native window wheel resizing/centering, full monitor and25% clamps, negative origins/high-resolution deltas, modifier/options guards and preserved source/reading state. No visible window/audio output.'
}finally{$hostWindow.Dispose();$window.Close()}
