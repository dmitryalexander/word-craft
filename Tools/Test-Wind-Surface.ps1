$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n");$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot');Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function SetProp($o,$name,$value){$o.GetType().GetProperty($name,$flags).SetValue($o,$value,$null)}
function PumpFor([int]$milliseconds){$frame=New-Object Windows.Threading.DispatcherFrame;$wake=New-Object Windows.Threading.DispatcherTimer;$wake.Interval=[TimeSpan]::FromMilliseconds($milliseconds);$wake.Add_Tick({$wake.Stop();$frame.Continue=$false}.GetNewClosure());$wake.Start();[Windows.Threading.Dispatcher]::PushFrame($frame)}
function CaptureSurface {
 $grid.UpdateLayout()
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$surface.ActualWidth),([int]$surface.ActualHeight),96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($surface);$pixels=New-Object byte[] ([int]$surface.ActualWidth*[int]$surface.ActualHeight*4);$bitmap.CopyPixels($pixels,[int]$surface.ActualWidth*4,0);return ,$pixels
}
Add-Type @'
public static class ClothPixelCheck {public static int Count(byte[] p){int count=0;for(int i=3;i<p.Length;i+=4)if(p[i]>2)count++;return count;}public static int Changed(byte[] a,byte[] b){int count=0;for(int i=0;i<a.Length;i+=4)if(System.Math.Abs(a[i+3]-b[i+3])>10)count++;return count;}}
'@
try {
 $grid.UpdateLayout();$surface=Field $window windSurface;$editor=Field $window editor;$material=Prop $surface MaterialRoot
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Cloth-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 SetField $surface FilePath (Join-Path $fixture 'Motion.json');SetField (Field $window characterCaret) FilePath (Join-Path $fixture 'Editing.json');SetField (Field (Field $window scene) Colors) FilePath (Join-Path $fixture 'Colors.json');SetField $window audioPath (Join-Path $fixture 'Audio.json')
 Check (!(Prop $surface Running)) 'Empty startup runs cloth'
 $source="The words are yours, moving like frayed canvas.`r`nA second line follows the simulated cloth. "+[Char]0x4e2d+' '+[Char]::ConvertFromUtf32(0x1f642)
 $editor.Text=$source;Call $window ShowFullEditor;PumpFor 850;SetProp $surface Paused $true;Call $material UpdateNow
 Check ((Prop $material ThreadCount) -gt 0) 'No loose canvas fibers generated'
 $revision=Prop $material FringeRevision;$maskRevision=Prop $material MaskRevision
 Call $surface ResetCloth;$mesh=Prop $surface SurfaceMesh;$camera=$surface.Camera;$before=CaptureSurface
 $watch=[Diagnostics.Stopwatch]::StartNew();Call $surface AdvanceSimulation @(240);$watch.Stop();$after=CaptureSurface
 Write-Output ('Native solver ms for 2 simulated seconds: '+$watch.ElapsedMilliseconds)
 Check ((Prop $surface PinError) -eq 0 -and (Prop $surface MaxDepth) -gt .01 -and (Prop $surface MaxDepth) -lt 4) 'Simulation unstable or pins moved'
 Check ([ClothPixelCheck]::Changed($before,$after) -gt 100) 'Simulated text has no visible motion'
 Check ((Prop $material FringeRevision) -eq $revision -and (Prop $material MaskRevision) -eq $maskRevision) 'Physics frames rebuild material cache'
 Check ([object]::ReferenceEquals($camera,$surface.Camera) -and $camera -is [Windows.Media.Media3D.OrthographicCamera] -and $camera.LookDirection.X -eq 0 -and $camera.LookDirection.Y -eq 0) 'Camera moved from fixed head-on view'
 foreach($corner in @(0,64,1820,1884)){Check ($mesh.Positions[$corner].Z -eq 0) 'Corner not pinned'}
 Call $surface SetLevelLines @($true)
 for($j=0;$j -le 28;$j++){Check ($mesh.Positions[$j*65].Y -eq $mesh.Positions[$j*65+64].Y) 'Level-lines switch bends baselines'}
 Call $surface SetLevelLines @($false)
 SetProp $surface TextOpacity 0;$zero=CaptureSurface;Check ([ClothPixelCheck]::Count($zero) -eq 0) 'Opacity zero still renders text'
 SetProp $surface ShowMesh $true;$wire=CaptureSurface;Check ([ClothPixelCheck]::Count($wire) -gt 100) 'Mesh diagnostic disappears with text opacity';SetProp $surface ShowMesh $false
 SetProp $surface TextOpacity .66;SetProp $surface Fraying 0;Call $material UpdateNow;$plain=CaptureSurface
 SetProp $surface TextureStrength 0;$untextured=CaptureSurface;SetProp $surface TextureStrength 1;$textured=CaptureSurface
 Check ([ClothPixelCheck]::Changed($untextured,$textured) -gt 100) 'Weave texture is not rendered'
 SetProp $surface Fraying 1;SetProp $surface FiberLength 12;Call $material UpdateNow;$frayed=CaptureSurface
 Check ((Prop $material ThreadCount) -gt 0 -and [ClothPixelCheck]::Count($frayed) -gt [ClothPixelCheck]::Count($plain)) 'Frays do not extend the text outline'
 SetProp $surface Fraying .71;SetProp $surface FiberLength 6.5;Call $material UpdateNow
 Draw "WordCraft-Cloth-text-$version.png"
 $hit=$grid.InputHitTest((New-Object Windows.Point 140,95));Check ($hit.GetType().FullName -eq 'System.Windows.Controls.TextBoxView') 'Native editor hit mapping lost'
 $editor.Select(4,5);$editor.SelectedText='letters';[void]$editor.Undo();Check ($editor.Text -eq $source) 'Material/physics changed native undo/source'
 Call $window ToggleSettings;$panel=Field $window settings
 foreach($pair in @(@('wind',1.3),@('turbulence',.72),@('tautness',.44),@('gravity',.65),@('texture',.84),@('weave',3.2),@('textOpacity',.59),@('fraying',.68),@('fiberLength',7.5))){(Field $panel $pair[0]).Value=[double]$pair[1]}
 Check ((Prop $surface Wind) -eq 1.3 -and (Prop $surface Turbulence) -eq .72 -and (Prop $surface Tautness) -eq .44 -and (Prop $surface Gravity) -eq .65 -and (Prop $surface TextureStrength) -eq .84 -and (Prop $surface WeaveSize) -eq 3.2 -and (Prop $surface TextOpacity) -eq .59 -and (Prop $surface Fraying) -eq .68 -and (Prop $surface FiberLength) -eq 7.5) 'One of nine sliders is disconnected'
 (Field $panel levelLines).IsChecked=$true;(Field $panel meshVisible).IsChecked=$true
 Check ((Prop $surface KeepLinesLevel) -and (Prop $surface ShowMesh)) 'Display toggles disconnected'
 $colorPanel=Field $panel colors;$scroll=$colorPanel.Child;$scroll.ScrollToVerticalOffset(160);$grid.UpdateLayout();Draw "WordCraft-Cloth-options-$version.png"
 $scroll.ScrollToVerticalOffset(450);$grid.UpdateLayout();Draw "WordCraft-Cloth-material-options-$version.png"
 Call $window CloseSettings
 $saved=Get-Content (Join-Path $fixture 'Motion.json') -Raw|ConvertFrom-Json
 Check ($saved.clothVersion -eq 1 -and $saved.gravity -eq .65 -and $saved.opacity -eq .59 -and $saved.levelLines -and $saved.showMesh) 'Complete settings did not save'
 $ctor=$assembly.GetType('WordCraftWindSurface').GetConstructor($flags,$null,[type[]]@([Windows.Controls.TextBox],[string]),$null)
 $box=New-Object Windows.Controls.TextBox;$arguments=New-Object 'System.Object[]' 2;$arguments[0]=$box.PSObject.BaseObject;$arguments[1]=[string](Join-Path $fixture 'Motion.json');$reload=$ctor.Invoke($arguments)
 Check ((Prop $reload Wind) -eq 1.3 -and (Prop $reload WeaveSize) -eq 3.2 -and (Prop $reload FiberLength) -eq 7.5 -and (Prop $reload KeepLinesLevel)) 'Saved cloth/material settings did not reload';Call $reload Dispose
 Call $surface ResetCloth;Check ((Prop $surface SimulationTime) -eq 0) 'Reset did not clear simulation history'
 SetProp $surface Paused $false;Check (Prop $surface Running) 'Cloth does not resume'
 Call $surface SetSuspended @($true);Check (!(Prop $surface Running)) 'Minimized cloth keeps running';Call $surface SetSuspended @($false)
 Call $window ShowReader @($false,$true);Check (!(Prop $surface Running)) 'Reading keeps full-text solver running'
 Call $window ShowFullEditor;PumpFor 120;Check ($surface.Opacity -gt 0 -and $surface.Opacity -lt 1) 'Ctrl+A entrance fade lost'
 Call $window ReturnToTyping;PumpFor 800;Check ($surface.Opacity -eq 0 -and !(Prop $surface Running) -and $editor.Text -eq $source) 'Typing exit lost cleanup/source'
 # Overflow remains a native scrolling document, while fiber draw objects stay bounded.
 Call $window ShowFullEditor;SetProp $surface Paused $true;Call $surface Show @($true,$false)
 $long='START '+('Words fill a scrolling cloth without losing the source. '*2000)+' END'
 $editor.FontSize=48;$editor.Text=$long;$editor.CaretIndex=$long.Length;$editor.ScrollToEnd();$grid.UpdateLayout();Call $material UpdateNow
 Check ($editor.ExtentHeight -gt $editor.ViewportHeight -and $editor.VerticalOffset -gt 0 -and $editor.Text -eq $long) 'Overflow text was truncated or did not scroll'
 Check ((Prop $material ThreadCount) -gt 128 -and (Prop $material FringeDrawCount) -le 128 -and (Prop $material FringeDrawCount) -gt 0) 'Dense text produces unbounded fiber draw objects'
 $revision=Prop $material FringeRevision;Call $material UpdateNow
 Check ((Prop $material FringeRevision) -eq $revision) 'Unchanged dense text rebuilt fibers'
 $editor.Select($long.Length-3,3);$editor.SelectedText='FIN';[void]$editor.Undo()
 Check ($editor.Text -eq $long) 'Dense cloth broke native undo'
 $editor.ScrollToHome();$grid.UpdateLayout();Call $material UpdateNow
 Check ($editor.VerticalOffset -eq 0 -and $editor.Text.StartsWith('START ') -and $editor.Text.EndsWith(' END')) 'Overflow lost the beginning or end'
 Draw ('WordCraft-Dense-Cloth-'+$version+'.png')
 $editor.Text=$source;Call $window ReturnToTyping
 'PASS: scrolling overflow/source/undo preserved, dense fiber drawing bounded to128 paths, native PBD evolution/fixed corners/camera, visible cloth, nine live sliders and save/reload, woven opacity/frayed silhouette/cache reuse, level toggle/reset/pause/suspend lifecycle, native click/Unicode undo/source and fades. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
