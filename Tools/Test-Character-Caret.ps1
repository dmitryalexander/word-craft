$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
Add-Type @'
public static class CharacterPixelCheck {
 public static int[] Selected(byte[] data){int red=0,opaque=0;for(int i=0;i<data.Length;i+=4)if(data[i+3]>100){opaque++;if(data[i+2]>data[i+1]*2)red++;}return new[]{red,opaque};}
 public static int[] Blink(byte[] a,byte[] b,int width,int left,int top,int right,int bottom){int changed=0,outside=0;for(int i=0;i<a.Length;i+=4){bool diff=false;for(int j=0;j<4;j++)diff|=a[i+j]!=b[i+j];if(diff){changed++;int x=(i/4)%width,y=(i/4)/width;if(x<left||x>right||y<top||y>bottom)outside++;}}return new[]{changed,outside};}
}
'@
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function SetProp($o,$name,$value){$o.GetType().GetProperty($name,$flags).SetValue($o,$value,$null)}
function CaptureEditor {
 $grid.UpdateLayout();$w=[int]$editor.ActualWidth;$h=[int]$editor.ActualHeight
 $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap $w,$h,96,96,([Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($editor);$pixels=[Array]::CreateInstance([byte],$w*$h*4);$bitmap.CopyPixels($pixels,$w*4,0)
 return @{pixels=$pixels;width=$w;height=$h}
}
try {
 $grid.UpdateLayout();$editor=Field $window editor;$caret=Field $window characterCaret
 $path=Join-Path $project ('Bleeding-Edge/Validation/Character-Caret-'+[guid]::NewGuid()+'.json')
 SetField $caret FilePath $path
 $accent='e'+[char]0x301;$emoji=[Char]::ConvertFromUtf32(0x1f642)
 $source='Alpha bravo '+$accent+' '+[char]0x4e2d+' '+$emoji+' omega.'+"`r`n"+'Second line stays editable.'
 $editor.Text=$source;Call $window ShowFullEditor;$grid.UpdateLayout()
 SetProp $caret SelectedColor ([Windows.Media.Color]::FromRgb(244,60,80))
 $editor.SelectAll();Call $caret Refresh @($true)
 $selectionKey=[Windows.Controls.Primitives.TextBoxBase].GetField('IsSelectionActivePropertyKey',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
 $editor.SetValue($selectionKey,$true)
 Check ($editor.CaretBrush.Color.A -eq 0 -and $editor.SelectionBrush.Color.A -eq 0) 'Caret/selection rectangle is still visible'
 Check ($editor.SelectionTextBrush.Color.R -eq 244 -and $editor.SelectionTextBrush.Color.G -eq 60) 'Selected glyph ink did not use custom color'
 Check ((Prop $caret BlinkIndex) -eq -1 -and $null -eq $editor.OpacityMask) 'Selected text still blinks'
 $selected=CaptureEditor;$stats=[CharacterPixelCheck]::Selected($selected.pixels);$red=$stats[0];$opaque=$stats[1]
 Draw "WordCraft-Character-selection-$version.png"
 Check ($red -gt 100) 'Rendered selected characters did not change color'
 Check ($opaque -lt $selected.width*$selected.height*.15) 'Selection rendered a filled rectangle'
 $editor.SetValue($selectionKey,$false)
 $inactive=CaptureEditor;$inactiveStats=[CharacterPixelCheck]::Selected($inactive.pixels)
 Check ($inactiveStats[0] -gt 100) 'Inactive selection lost its custom character color'
 Draw "WordCraft-Character-selection-$version.png"
 $editor.Select(0,0);Call $caret Refresh @($true)
 Check ((Prop $caret BlinkIndex) -eq 0 -and (Prop $caret BlinkLength) -eq 1 -and $null -ne $editor.OpacityMask) 'Letter after insertion point is not masked for blink'
 $brush=Field $caret blink;$brush.BeginAnimation([Windows.Media.Brush]::OpacityProperty,$null)
 $brush.Opacity=1;$on=CaptureEditor;$brush.Opacity=0;$off=CaptureEditor
 $rect=Prop $caret BlinkRect
 $stats=[CharacterPixelCheck]::Blink($on.pixels,$off.pixels,$on.width,[int]($rect.Left-2),[int]($rect.Top-2),[int]($rect.Right+2),[int]($rect.Bottom+2))
 Check ($stats[0] -gt 10 -and $stats[1] -eq 0) 'Blink failed to hide only the next character pixels'
 foreach($span in @(@($source.IndexOf($accent),2),@($source.IndexOf($emoji),2))){
  $editor.Select([int]$span[0],0);Call $caret Refresh @($true)
  Check ((Prop $caret BlinkLength) -eq $span[1]) 'Combining/surrogate character was split'
 }
 $editor.Select($source.Length,0);Call $caret Refresh @($true)
 Check ((Prop $caret BlinkIndex) -eq -1 -and $null -eq $editor.OpacityMask) 'End of text gained a phantom character/caret'
 $editor.Select(6,0);$editor.SelectedText='X';[void]$editor.Undo()
 Check ($editor.Text -eq $source) 'Presentation changes polluted native editing undo'
 Check (Call $caret Save) 'Selection color did not save'
 $saved=Get-Content $path -Raw|ConvertFrom-Json
 Check ($saved.selectedCharacters -eq '#F43C50') 'Saved selection color wrong'
 $box=New-Object Windows.Controls.TextBox
 $arguments=New-Object 'System.Object[]' 2;$arguments[0]=$box.PSObject.BaseObject;$arguments[1]=[string]$path
 $constructor=$assembly.GetType('WordCraftCharacterCaret').GetConstructor($flags,$null,[type[]]@([Windows.Controls.TextBox],[string]),$null)
 $other=$constructor.Invoke($arguments)
 Check ((Prop $other SelectedColor).R -eq 244 -and (Prop $other SelectedColor).G -eq 60) 'Selection color did not reload'
 Call $other Dispose
 $scene=Field $window scene;$profiles=Field $scene Colors
 SetField $profiles FilePath ($path+'.palette.json')
 Call $window ToggleSettings
 $panel=Field $window settings;$colors=Field $panel colors
 $body=$colors.Child.Content
 $expander=$body.Children|Where-Object {$_ -is [Windows.Controls.Expander] -and $_.Header -eq 'Selected characters'}|Select-Object -First 1
 Check ($null -ne $expander) 'Selected character color option missing'
 $expander.IsExpanded=$true;$picker=$expander.Content.Children[0]
 Call $picker SetColor @([Windows.Media.Color]::FromRgb(23,190,210));Call $picker Publish
 Check ($editor.SelectionTextBrush.Color.G -eq 190) 'Options picker did not update selected-character color'
 Draw "WordCraft-Character-options-$version.png"
 Call $window CloseSettings
 Call $window ShowReader @($false,$true)
 Check ((Prop $caret BlinkIndex) -eq -1 -and $null -eq $editor.OpacityMask) 'Hidden editor kept blinking'
 Check ($editor.Text -eq $source) 'Caret/selection presentation changed source'
 'PASS: invisible caret/selection background, actual selected-glyph color pixels, native glyph blink mask, Unicode clusters, document end, ordinary editing undo, hidden-view cleanup and isolated color save. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
