$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$app=Join-Path $project 'Bleeding-Edge/App'
Add-Type @'
using System.Runtime.InteropServices;
public static class WordSmokeFixtureNative {
 [DllImport("kernel32",CharSet=CharSet.Unicode)]public static extern bool SetDllDirectory(string path);
}
'@
[void][WordSmokeFixtureNative]::SetDllDirectory($app)
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $app 'WordCraft.exe'))
$version=$assembly.GetName().Version.ToString(3)
$flags=[Reflection.BindingFlags]'Public,NonPublic,Instance'
function Field($o,$name){$o.GetType().GetField($name,$flags).GetValue($o)}
function Prop($o,$name){$o.GetType().GetProperty($name,$flags).GetValue($o,$null)}
function Call($o,$name,[object[]]$values){$o.GetType().GetMethod($name,$flags).Invoke($o,$values)}
function Check($ok,$message){if(!$ok){throw $message}}
$window=[Activator]::CreateInstance($assembly.GetType('WordCraftEditor'),$flags,$null,[object[]]@($false),$null)
$grid=$window.Content;$window.Content=$null
$parameters=New-Object Windows.Interop.HwndSourceParameters 'Smoke WordCraft fixture'
$parameters.Width=1440;$parameters.Height=900;$parameters.PositionX=-30000;$parameters.PositionY=-30000
$parameters.WindowStyle=-1879048192
$hostWindow=New-Object Windows.Interop.HwndSource $parameters
$hostWindow.RootVisual=$grid
function Draw($name){
 $grid.UpdateLayout()
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$grid.ActualWidth),([int]$grid.ActualHeight),96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($grid)
 $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create((Join-Path $project ('Bleeding-Edge/Validation/'+$name)))
 try{$encoder.Save($stream)}finally{$stream.Dispose()}
}
try {
 $grid.UpdateLayout();$editor=Field $window editor
 Check ($editor.Text -eq '' -and !(Field $window reading) -and $editor.Opacity -eq 0) 'Startup is not an empty smoke writer'
 Check ($null -eq (Field $window flow)) 'Reader eagerly created at startup'
 foreach($name in 'CodexCraftClient','DesktopChatConnection'){
  Check ($null -eq $assembly.GetType($name)) "Unwanted chat component compiled: $name"
 }
 $scene=Field $window scene
 Draw "WordCraft-Idle-$version.png"
 $smoke=Field $scene smoke;$gpu=Prop $smoke Paint
 Check ($null -ne $gpu -and !(Prop $gpu Failed)) 'Native smoke GPU was not initialized'
 Check ((Field $gpu flowHeight) -ge 144) 'Smoke quality grid not initialized'
 Check ((Get-Process -Id $PID).Modules.ModuleName -contains 'WordCraftSmoke.dll') 'Dedicated smoke DLL not loaded'
 for($i=0;$i -lt 12;$i++){Call $gpu Advance}
 Check (!(Prop $gpu Failed) -and (Prop $gpu Milliseconds) -gt 0) 'Native smoke did not complete GPU frames'
 Check ((Prop $scene Phase) -eq 0) 'Idle did not use typing palette'
 $editor.Text='The words are yours.'
 Call $scene DepositTypedVersion @('The ','words','')
 Check ((Field $scene depositedWord) -eq 'The words') 'Typing did not deposit glyphs'
 $editor.Select(4,5)
 Call $window PasteText @('sentences')
 Check ($editor.Text -eq 'The sentences are yours.') 'Paste did not replace only selection'
 Check ((Field $window reading)) 'Paste did not open reading'
 $flow=Field $window flow;$reader=Prop $flow Reader
 Check ($reader.PlaybackDirection -eq 1) 'Paste did not start playback'
 Check ((Prop $scene Phase) -eq 2) 'Reading did not use reply smoke palette'
 Check ((Prop $smoke MinimumInkFrameMilliseconds) -eq 33) 'Reading lost smoke frame budget'
 Check ($null -eq (Field $reader narration)) 'Narration engine unexpectedly exists'
 $reader.PausePlayback();(Field $window timer).Stop()
 Check ((Prop $flow TextLength) -eq $editor.Text.Length) 'Reader lost source text'
 Call $window ShowEditor @(1)
 Check ($editor.CaretIndex -eq 13 -and !(Field $window reading)) 'Click-to-edit word position failed'
 $editor.Select(4,9);$editor.SelectedText='words'
 Call $window ShowReader @($false,$true)
 Check ((Field $window readerText) -eq 'The words are yours.') 'Subtraction/edit was not used by reader'
 Check ($reader.PlaybackDirection -eq 0) 'Paused reader unexpectedly plays'
 Call $window ShowEditor @(-1)
 $editor.Text="# A quiet place`r`n`r`nWrite, change, and read your words. 日本語を読む。"
 $original=$editor.Text
 Check ($original.Contains([string][char]0x65e5)) 'Fixture Japanese text encoding is wrong'
 Call $window ShowReader @($false,$true)
 Check ($editor.Text -eq $original) 'Markdown/Unicode source changed'
 Check ($reader.FontFamilyName -eq 'Yu Mincho') 'Japanese-capable font not selected'
 for($i=0;$i -lt 8;$i++){[void]$reader.Step(1)}
 for($i=0;$i -lt 15;$i++){[void](Call $reader Advance @(0.035))}
 Call (Field $window readingView) UpdateFrame
 for($i=0;$i -lt 12;$i++){Call $gpu Advance}
 Draw "WordCraft-Reading-$version.png"

 # A real routed text composition returns to edit; the TextBox handles insertion normally.
 $composition=New-Object Windows.Input.TextComposition ([Windows.Input.InputManager]::Current),$editor,'追加'
 $args=New-Object Windows.Input.TextCompositionEventArgs ([Windows.Input.Keyboard]::PrimaryDevice),$composition
 $args.RoutedEvent=[Windows.Input.TextCompositionManager]::PreviewTextInputEvent
 $editor.RaiseEvent($args)
 Check (!(Field $window reading) -and !$args.Handled) 'Typing did not return to editor without eating input'
 $editor.SelectedText='追加'
 Check ($editor.Text.Contains('追加')) 'Composed text was not editable'
 Check ((Prop $scene Phase) -eq 0) 'Typing did not restore its smoke palette'
 Check (!(Prop $scene ReplySmokePending)) 'Old reading deposits leaked into typing'
 for($i=0;$i -lt 12;$i++){Call $gpu Advance}
 Draw "WordCraft-Writing-$version.png"
 Call $window ShowFullEditor
 Check ($editor.Opacity -eq 1 -and $editor.IsHitTestVisible) 'Full-text editing is not available'
 Draw "WordCraft-Full-Editor-$version.png"
 $editor.Select(2,0);$caret=$editor.CaretIndex
 Call $window BeginScrub
 Call $window SeekScrub @($grid.ActualWidth*0.8)
 Check ($reader.PlaybackDirection -eq 0) 'Scrub failed to pause'
 Call $window EndGesture @($true,$false)
 Check (!(Field $window reading) -and $editor.CaretIndex -eq $caret) 'Scrub cancellation lost editor/caret'
 Check ($editor.Opacity -eq 1) 'Scrub cancellation lost full-text editing view'
 Call $window ShowReader @($true,$true)
 Call $window BeginScrub
 Call $window SeekScrub @($grid.ActualWidth*0.5)
 Call $window EndGesture @($false,$true)
 Check ($reader.PlaybackDirection -eq 1) 'Scrub did not resume active reading'
 $reader.PausePlayback();(Field $window timer).Stop()
 Check ((Field $reader SlowPlaybackFactor) -eq 0.33) 'Shift slow factor changed'
 Check ($null -eq $assembly.GetType('PlainReadingView')) 'Old flat reader was compiled'
 'PASS: native smoke GPU and dedicated DLL, idle/typing/read smoke, no chat, paste/read/edit/Unicode preservation, scrub cancel/resume/caret and33% slow reading. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
