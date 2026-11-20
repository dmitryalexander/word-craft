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
 $view=Field $window simulationView
 Check ($view.Clip -is [Windows.Media.RectangleGeometry] -and $view.Clip.RadiusX -eq 20) 'Inset simulation lacks rounded clipping'
 foreach($offset in @(@(1,1),@(([int]$rect.Width-2),1),@(1,([int]$rect.Height-2)),@(([int]$rect.Width-2),([int]$rect.Height-2)))){
  $x=[int]$rect.X+$offset[0];$y=[int]$rect.Y+$offset[1];$i=($y*[int]$grid.ActualWidth+$x)*4
  Check ($pixels[$i] -eq 0 -and $pixels[$i+1] -eq 0 -and $pixels[$i+2] -eq 0) 'Rounded display corner did not reveal black'
 }
 $i=(([int]$rect.Y+24)*[int]$grid.ActualWidth+[int]$rect.X+24)*4
 Check (($pixels[$i]+$pixels[$i+1]+$pixels[$i+2]) -gt 0) 'Rounded clipping removed simulation interior'
 $font=$editor.FontSize
 Check (Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control)) 'Smoke Ctrl+wheel was not reserved'
 Check ($editor.FontSize -eq $font) 'Smoke Ctrl+wheel resized hidden mesh text'
 Call $window ShowFullEditor
 $surface=Field $window windSurface;$surface.GetType().GetProperty('Paused',$flags).SetValue($surface,$true,$null)
 Call $surface Show @($true,$false)
 $grid.UpdateLayout();$material=Prop $surface MaterialRoot;Call $material UpdateNow
 $revision=Prop $material FringeRevision
 $original=$editor.Text;$editor.CaretIndex=$editor.Text.Length;$editor.SelectedText=' added';$source=$editor.Text
 $editor.Select(5,4);$selection=@($editor.SelectionStart,$editor.SelectionLength);$undo=$editor.CanUndo
 Check $undo 'Native undo fixture has no edit to preserve'
 $glyphBefore=$editor.GetRectFromCharacterIndex(0)
 Check (Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control)) 'Mesh Ctrl+wheel not consumed'
 $grid.UpdateLayout();$glyphAfter=$editor.GetRectFromCharacterIndex(0)
 Check ([Math]::Abs($editor.FontSize-$font*1.1) -lt 0.001 -and $glyphAfter.Height -gt $glyphBefore.Height) 'Actual native glyphs did not grow'
 Call $material UpdateNow
 Check ((Prop $material FringeRevision) -gt $revision) 'Font change did not invalidate cached material fringe'
 Check ($editor.Text -eq $source -and $editor.SelectionStart -eq $selection[0] -and $editor.SelectionLength -eq $selection[1] -and $editor.CanUndo -eq $undo -and (Prop $window SimulationZoom) -eq 0.25) 'Font zoom changed source/selection/undo or simulation zoom'
 Call $window HandleMeshFontWheel @(-120,[Windows.Input.ModifierKeys]::Control)|Out-Null
 Check ([Math]::Abs($editor.FontSize-$font) -lt 0.001) 'Reverse font wheel did not shrink back'
 Call $window HandleMeshFontWheel @(30,[Windows.Input.ModifierKeys]::Control)|Out-Null
 Check ($editor.FontSize -gt $font -and $editor.FontSize -lt $font*1.1) 'Fractional font wheel lost'
 Call $window HandleMeshFontWheel @(12000,[Windows.Input.ModifierKeys]::Control)|Out-Null
 Check ($editor.FontSize -eq 160) 'Font maximum exceeded'
 Call $window HandleMeshFontWheel @(-12000,[Windows.Input.ModifierKeys]::Control)|Out-Null
 Check ($editor.FontSize -eq 8) 'Font minimum exceeded'
 # Capture actual enlarged native mesh text inside the rounded inset.
 $editor.FontSize=42;Call $material InvalidateTextLayout
 Call $window SetSimulationZoom @([double]0.75);$grid.UpdateLayout();Call $material UpdateNow
 Draw ('WordCraft-Rounded-Mesh-Font-'+$version+'.png')
 Call $window SetSimulationZoom @([double]0.25)
 foreach($name in 'volumeDrag','scrubbing'){
  SetField $window $name $true
  Check (!(Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control))) 'Active gesture was interrupted by font zoom'
  SetField $window $name $false
 }
 Check (!(Call $window HandleMeshFontWheel @(120,([Windows.Input.ModifierKeys]::Control -bor [Windows.Input.ModifierKeys]::Shift)))) 'Ctrl+Shift wheel resized font'
 $help=Field $window help;Call $help Show
 Check (!(Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control))) 'Help scrolling was stolen by font zoom'
 Call $help Hide @($false)
 Call $window HandleSimulationZoomWheel @(30,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ((Prop $window SimulationZoom) -gt 0.25) 'Fractional wheel delta lost'
 Call $window ShowReader @($false,$true);$reader=Prop (Field $window flow) Reader;$reader.Seek(13);$anchor=$reader.TextAnchor
 Call $window HandleSimulationZoomWheel @(120,[Windows.Input.ModifierKeys]::None)|Out-Null
 Check ($reader.TextAnchor -eq $anchor -and $reader.PlaybackDirection -eq 0 -and $editor.Text -eq $source) 'Zoom changed text or reading state'
 $font=$editor.FontSize
 Check (Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control)) 'Reading Ctrl+wheel escaped to reader resize'
 Check ($editor.FontSize -eq $font -and $reader.TextAnchor -eq $anchor -and $reader.PlaybackDirection -eq 0) 'Reading Ctrl+wheel changed font or playback'
 $before=Prop $window SimulationZoom
 foreach($modifier in [Windows.Input.ModifierKeys]::Control,[Windows.Input.ModifierKeys]::Shift,[Windows.Input.ModifierKeys]::Alt){Check (!(Call $window HandleSimulationZoomWheel @(120,$modifier))) 'Modified wheel captured'}
 Call $window ToggleSettings
 Check ((Field $window settings).Parent -eq $grid -and (Field $window help).Parent -eq $grid) 'Options/help are inside the shrinking simulation'
 Check (!(Call $window HandleSimulationZoomWheel @(120,[Windows.Input.ModifierKeys]::None)) -and (Prop $window SimulationZoom) -eq $before) 'Options scrolling was stolen'
 Check (!(Call $window HandleMeshFontWheel @(120,[Windows.Input.ModifierKeys]::Control))) 'Options scrolling was stolen by font zoom'
 Call $window CloseSettings
 Call $window HandleSimulationZoomWheel @(12000,[Windows.Input.ModifierKeys]::None)|Out-Null
 $rect=Geometry;Check ((Prop $window SimulationZoom) -eq 1 -and [Math]::Abs($rect.Width-$grid.ActualWidth) -lt 0.01) 'Zoom exceeded or failed 100 percent'
 Check ($null -eq $view.Clip) 'Full-size simulation still has rounded black cutouts'
 $editor.Undo();Check ($editor.Text -eq $original) 'Font resizing broke native text undo'
 $boundsAfter=@($window.Left,$window.Top,$window.Width,$window.Height,$window.WindowState,$window.WindowStyle,$window.ResizeMode,$window.MinWidth,$window.MinHeight)
 Check (($boundsBefore -join '|') -eq ($boundsAfter -join '|') -and (New-Object Windows.Interop.WindowInteropHelper $window).Handle -eq $nativeBefore) 'Zoom changed/created native window geometry or style'
 'PASS: centered25-100% simulation-only scaling, rounded black surround pixels, native mesh-only font sizing/material refresh with source-selection-undo preservation, unchanged native window/layout/GPU resolution, mapped coordinates, full-size options/help and preserved text/reading/gesture guards. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
