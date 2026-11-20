$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n");$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot');Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function Geometry {
 $grid.UpdateLayout();$canvas=Field $window canvas
 return $canvas.TransformToAncestor($grid).TransformBounds((New-Object Windows.Rect 0,0,$canvas.ActualWidth,$canvas.ActualHeight))
}
try {
 $grid.UpdateLayout();$canvas=Field $window canvas;$scene=Field $window scene;$smoke=Field $scene smoke;$gpu=Field $smoke gpuInk
 $editor=Field $window editor;$editor.Text='CAPS LOCK stays intact while only the simulation scales.';$source=$editor.Text
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Simulation-Zoom-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 SetField (Field $scene Colors) FilePath (Join-Path $fixture 'Colors.json');SetField $window audioPath (Join-Path $fixture 'Audio.json')
 $boundsBefore=@($window.Left,$window.Top,$window.Width,$window.Height,$window.WindowState,$window.WindowStyle,$window.ResizeMode,$window.MinWidth,$window.MinHeight)
 $nativeBefore=(New-Object Windows.Interop.WindowInteropHelper $window).Handle
 $logicalWidth=$canvas.ActualWidth;$logicalHeight=$canvas.ActualHeight;$gpuBefore=Prop $gpu Grid
 Check ((Prop $window SimulationZoom) -eq 1) 'Startup is not full simulation size'
 Check (Call $window HandleSimulationZoomWheel @(-120,[Windows.Input.ModifierKeys]::None)) 'Wheel not consumed'
 Check ([Math]::Abs((Prop $window SimulationZoom)-0.95) -lt 0.00001) 'Wheel step is not 5 percent'
 Call $window HandleSimulationZoomWheel @(-12000,[Windows.Input.ModifierKeys]::None)|Out-Null
 $rect=Geometry
 Check ([Math]::Abs($rect.Width-$grid.ActualWidth*0.25) -lt 0.01 -and [Math]::Abs($rect.Height-$grid.ActualHeight*0.25) -lt 0.01) 'Simulation did not reach 25 percent'
 Check ([Math]::Abs($rect.X-$grid.ActualWidth*0.375) -lt 0.01 -and [Math]::Abs($rect.Y-$grid.ActualHeight*0.375) -lt 0.01) 'Simulation is not centered'
 Check ($canvas.ActualWidth -eq $logicalWidth -and $canvas.ActualHeight -eq $logicalHeight -and (Prop $gpu Grid) -eq $gpuBefore) 'Presentation zoom changed scene layout or GPU resolution'
 $mapped=$canvas.TransformToAncestor($grid).Transform((New-Object Windows.Point ($logicalWidth/2),($logicalHeight/2)))
 Check ([Math]::Abs($mapped.X-$grid.ActualWidth/2) -lt 0.01) 'Input/presentation center mapping changed'
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$grid.ActualWidth),([int]$grid.ActualHeight),96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($grid);$pixels=New-Object byte[] ([int]$grid.ActualWidth*[int]$grid.ActualHeight*4);$bitmap.CopyPixels($pixels,[int]$grid.ActualWidth*4,0)
 foreach($point in @(@(2,2),@(([int]$grid.ActualWidth-3),2),@(2,([int]$grid.ActualHeight-3)))){$i=([int]$point[1]*[int]$grid.ActualWidth+[int]$point[0])*4;Check ($pixels[$i] -eq 0 -and $pixels[$i+1] -eq 0 -and $pixels[$i+2] -eq 0 -and $pixels[$i+3] -eq 255) 'Outer fill is not opaque black'}
 Call $window ShowFullEditor
 $surface=Field $window windSurface;$surface.GetType().GetProperty('Paused',$flags).SetValue($surface,$true,$null)
 Call $surface Show @($true,$false)
 Draw ('WordCraft-Simulation-Zoom-quarter-'+$version+'.png')
 Call $window HandleSimulationZoomWheel @(30,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ((Prop $window SimulationZoom) -gt 0.25) 'Fractional wheel delta lost'
 Call $window ShowReader @($false,$true);$reader=Prop (Field $window flow) Reader;$reader.Seek(13);$anchor=$reader.TextAnchor
 Call $window HandleSimulationZoomWheel @(120,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ($reader.TextAnchor -eq $anchor -and $reader.PlaybackDirection -eq 0 -and $editor.Text -eq $source) 'Zoom changed text or reading state'
 $before=Prop $window SimulationZoom
 foreach($modifier in [Windows.Input.ModifierKeys]::Control,[Windows.Input.ModifierKeys]::Shift,[Windows.Input.ModifierKeys]::Alt){Check (!(Call $window HandleSimulationZoomWheel @(120,$modifier))) 'Modified wheel captured'}
 Call $window ToggleSettings
 Check ((Field $window settings).Parent -eq $grid -and (Field $window help).Parent -eq $grid) 'Options/help are inside the shrinking simulation'
 Check (!(Call $window HandleSimulationZoomWheel @(120,[Windows.Input.ModifierKeys]::None)) -and (Prop $window SimulationZoom) -eq $before) 'Options scrolling was stolen'
 Call $window CloseSettings
 Call $window HandleSimulationZoomWheel @(12000,[Windows.Input.ModifierKeys]::None)|Out-Null
 $rect=Geometry;Check ((Prop $window SimulationZoom) -eq 1 -and [Math]::Abs($rect.Width-$grid.ActualWidth) -lt 0.01) 'Zoom exceeded or failed 100 percent'
 $boundsAfter=@($window.Left,$window.Top,$window.Width,$window.Height,$window.WindowState,$window.WindowStyle,$window.ResizeMode,$window.MinWidth,$window.MinHeight)
 Check (($boundsBefore -join '|') -eq ($boundsAfter -join '|') -and (New-Object Windows.Interop.WindowInteropHelper $window).Handle -eq $nativeBefore) 'Zoom changed/created native window geometry or style'
 'PASS: centered25-100% simulation-only scaling, black surround pixels, unchanged native window/layout/GPU resolution, mapped coordinates, full-size options/help and preserved text/reading/gesture guards. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
