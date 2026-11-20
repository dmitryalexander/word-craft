$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n");$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot');Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function PumpFor([int]$ms){$frame=New-Object Windows.Threading.DispatcherFrame;$wake=New-Object Windows.Threading.DispatcherTimer;$wake.Interval=[TimeSpan]::FromMilliseconds($ms);$wake.Add_Tick({$wake.Stop();$frame.Continue=$false}.GetNewClosure());$wake.Start();[Windows.Threading.Dispatcher]::PushFrame($frame)}
function KeyEvent($key){$ctor=[Windows.Input.KeyEventArgs].GetConstructor([type[]]@([Windows.Input.KeyboardDevice],[Windows.PresentationSource],[int],[Windows.Input.Key]));$arguments=New-Object object[] 4;$arguments[0]=[Windows.Input.Keyboard]::PrimaryDevice;$arguments[1]=$hostWindow.PSObject.BaseObject;$arguments[2]=[int]0;$arguments[3]=[Windows.Input.Key]$key;$result=$ctor.Invoke($arguments);$result.RoutedEvent=[Windows.Input.Keyboard]::PreviewKeyDownEvent;return $result}
try {
 $grid.UpdateLayout();$help=Field $window help;$editor=Field $window editor
 Check (!(Prop $help IsOpen) -and $help.Visibility -eq 'Collapsed') 'Controls appeared on empty startup'
 $editor.Text='The simulation remains behind a quiet keyboard guide.';$source=$editor.Text
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Controls-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 SetField (Field (Field $window scene) Colors) FilePath (Join-Path $fixture 'Colors.json');SetField $window audioPath (Join-Path $fixture 'Audio.json')
 $down=KeyEvent ([Windows.Input.Key]::F1);Call $window OnPreviewKeyDown @($down)
 Check ($down.Handled -and (Prop $help IsOpen) -and  $help.HasAnimatedProperties -and $help.Visibility -eq [Windows.Visibility]::Visible) 'F1 did not open with a fade'
 PumpFor 200;$grid.UpdateLayout()
 Check ((Prop $help KeycapCount) -ge 30) 'Keyboard cap guide is incomplete'
 Check ($help.Background.Color.A -gt 100 -and $help.Background.Color.A -lt 255) 'Backdrop is not translucent'
 Check ($help.ActualWidth -eq $grid.ActualWidth -and $help.ActualHeight -eq $grid.ActualHeight) 'Controls do not cover the whole window'
 $rows=Field $help rows
 foreach($row in $rows){$keys=$row.Children[0];$description=$row.Children[1];if([Windows.Controls.Grid]::GetRow($description) -eq 0){Check ($description.TranslatePoint((New-Object Windows.Point 0,0),$row).X -ge $keys.ActualWidth+11.9) 'A key chord overlaps its description';Check ($description.TextAlignment -eq 'Left' -and $keys.HorizontalAlignment -eq 'Left') 'Controls are not left aligned'}}
 $scroll=Field $help scroll;$origin=$scroll.TranslatePoint((New-Object Windows.Point 0,0),$help)
 Check ([Math]::Abs($origin.X-($help.ActualWidth-$origin.X-$scroll.ActualWidth)) -lt 1) 'Controls block has unequal left/right padding'
 Check ([Math]::Abs($origin.Y-($help.ActualHeight-$origin.Y-$scroll.ActualHeight)) -lt 1) 'Controls block has unequal top/bottom padding'
 Check ($scroll.ActualHeight -lt $help.ActualHeight-64) 'Guide stretched vertically instead of centering content'
 $letters=@();foreach($row in $rows){foreach($item in $row.Children[0].Children){if($item -is [Windows.Controls.Border]){$letters+=$item.Child.Child.Text;Check ($item.CornerRadius.TopLeft -gt 0 -and $item.Child.BorderThickness.Left -gt 0) 'A keycap lost its rounded physical shape'}}}
 Check ($letters -contains 'A' -and $letters -contains 'Ctrl' -and $letters -contains 'Space') 'Expected real key labels missing'
 Check (!(Call $window HandleSimulationZoomWheel @(-120,[Windows.Input.ModifierKeys]::None))) 'Help wheel resized the simulation'
 $key=KeyEvent ([Windows.Input.Key]::N);Call $window OnPreviewKeyDown @($key);Check ($key.Handled -and $editor.Text -eq $source) 'Help key input leaked into the document'
 Draw ('WordCraft-Keycap-Controls-'+$version+'.png')
 $up=KeyEvent ([Windows.Input.Key]::F1);$up.RoutedEvent=[Windows.Input.Keyboard]::PreviewKeyUpEvent;Call $window OnPreviewKeyUp @($up)
 Check ($up.Handled -and (Prop $help IsOpen)) 'F1 release closed the toggled guide'
 Call $window OnPreviewKeyDown @((KeyEvent ([Windows.Input.Key]::F1)))
 Check (!(Prop $help IsOpen) -and $help.HasAnimatedProperties -and $help.Visibility -eq [Windows.Visibility]::Visible) 'Second F1 press did not fade out'
 $up=KeyEvent ([Windows.Input.Key]::F1);$up.RoutedEvent=[Windows.Input.Keyboard]::PreviewKeyUpEvent;Call $window OnPreviewKeyUp @($up)
 Check (!(Prop $help IsOpen)) 'F1 release reopened the guide' 
 Call $help Show;PumpFor 200;Check ((Prop $help IsOpen) -and $help.Visibility -eq 'Visible') 'Old fade hid reopened controls'
 $escape=KeyEvent ([Windows.Input.Key]::Escape);Call $window OnPreviewKeyDown @($escape);PumpFor 170
 Check (!(Prop $help IsOpen) -and !(Field $window closed)) 'Escape closed app instead of guide'
 Call $window ToggleSettings;Call $window OnPreviewKeyDown @((KeyEvent ([Windows.Input.Key]::F1)));PumpFor 220
 Check ((Prop $help IsOpen) -and [Windows.Controls.Panel]::GetZIndex($help) -gt [Windows.Controls.Panel]::GetZIndex((Field $window settings))) 'F1 does not work above options'
 Call $help Hide @($true);Call $window CloseSettings
 $grid.Width=640;$grid.Height=480;$hostWindow.SizeToContent=[Windows.SizeToContent]::WidthAndHeight;$grid.UpdateLayout();Call $help Show;PumpFor 220;$grid.UpdateLayout()
 Check ((Field $help scroll).ScrollableHeight -gt 0) 'Small-window controls cannot scroll'
 Draw ('WordCraft-Keycap-Controls-small-'+$version+'.png')
 Call $help Hide @($true);Check ($help.Visibility -eq 'Collapsed' -and $help.Opacity -eq 0) 'Immediate cleanup failed'
 Check ($editor.Text -eq $source) 'Controls changed source'
 'PASS: centered controls with equal opposite padding, left-aligned labels beside fitted key chords, native rounded keycaps, spaced chord rows, full-window translucent backdrop, real F1 toggle/release/escape, fade reversal, input guards, options layering, small-screen scrolling and empty startup. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
