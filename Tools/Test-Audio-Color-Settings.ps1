$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function SetProp($o,$name,$value){$o.GetType().GetProperty($name,$flags).SetValue($o,$value,$null)}
$workspace=Split-Path $project -Parent
$chatColors=Join-Path $project 'Defaults/Color-Tuning.json'
$ownColors=Join-Path $project 'Bleeding-Edge/Color-Tuning.json'
$chatHash=(Get-FileHash $chatColors).Hash;$ownHash=(Get-FileHash $ownColors).Hash
$fixture=Join-Path $project ('Bleeding-Edge/Validation/Settings-'+[guid]::NewGuid())
New-Item -ItemType Directory -Path $fixture|Out-Null
$testColors=Join-Path $fixture 'Color-Tuning.json';$testAudio=Join-Path $fixture 'Audio-Settings.json'
try {
 $grid.UpdateLayout();$sound=Field $window sound;$scene=Field $window scene;$profiles=Field $scene Colors
 Check ([IO.Path]::GetFullPath((Field $profiles FilePath)) -eq [IO.Path]::GetFullPath($ownColors)) 'WordCraft palette path is not app-local'
 Check ((Field $profiles FilePath) -ne $chatColors) 'WordCraft shares ChatCraft color storage'
 SetField $profiles FilePath $testColors;SetField $window audioPath $testAudio
 SetProp $sound Volume ([double]0.8)
 $editor=Field $window editor;$editor.Text='Balance the layers while the words keep moving.'
 Call $window ShowReader @($true,$true)
 $flow=Field $window flow;$reader=Prop $flow Reader;$reader.SetSpeed(1)
 Call $window ToggleSettings
 $panel=Field $window settings
 Check ($null -ne $panel) 'Caps settings panel failed to open'
 Check ($reader.PlaybackDirection -eq 1) 'Opening settings interrupted listening/playback'
 $morse=Field $panel morse;$noise=Field $panel noise
 $morse.Value=23;$noise.Value=67; (Field $panel secondaryNoise).Value=29; (Field $panel primaryKinds)[2].RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Primitives.ButtonBase]::ClickEvent))); (Field $panel secondaryKinds)[1].RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Primitives.ButtonBase]::ClickEvent))); (Field $panel secondaryWhileReading).IsChecked=$true; (Field $panel playbackOnly).IsChecked=$false
 Check ([Math]::Abs((Prop $sound MorseVolume)-0.23) -lt .00001) 'Morse slider did not change Morse gain'
 Check ([Math]::Abs((Prop $sound NoiseVolume)-0.67) -lt .00001) 'Procedural slider did not change its gain'
 $typing=Field $sound typing
 Check ([Math]::Abs((Prop $typing Volume)-0.184) -lt .00001) 'Morse master/layer gain product wrong'
 $noise.Value=0
 Check ([Math]::Abs((Prop $typing Volume)-0.184) -lt .00001) 'Muting noise altered Morse gain'
 $noise.Value=67;$morse.Value=0
 Check ((Prop $typing Volume) -eq 0 -and (Prop $sound NoiseVolume) -eq 0.67) 'Morse mute altered noise gain'
 $morse.Value=23
 $primary=Field $panel primaryKinds;$secondary=Field $panel secondaryKinds
 Check ($primary[2].IsChecked -and !$primary[0].IsChecked -and !$primary[1].IsChecked) 'Primary buttons do not select exclusively'
 Check ($secondary[1].IsChecked -and !$secondary[0].IsChecked -and !$secondary[2].IsChecked) 'Secondary buttons do not select exclusively'
 Check ((Prop $sound NoiseKind) -eq 2 -and (Prop $sound SecondaryNoiseKind) -eq 1 -and (Prop $sound NoiseSecondaryWhileReading)) 'Noise buttons/toggle did not configure audio'
 $colors=Field $panel colors;$phases=Field $colors phases
 Check ($phases[1].Visibility -eq 'Collapsed' -and $phases[2].Content -eq 'Reading') 'WordCraft exposes wrong phase controls'
 Call $profiles Set @(0,'background',[Windows.Media.Color]::FromRgb(14,35,48))
 Call $profiles Set @(2,'text',[Windows.Media.Color]::FromRgb(180,230,220))
 $ink=Prop $reader SingleLineInk
 Check ($ink.R -eq 180 -and $ink.G -eq 230) 'Reading text color did not preview live'
 Draw "WordCraft-Settings-$version.png"
 Call $window CloseSettings
 Check ($null -eq (Field $window settings)) 'Panel did not save/close'
 Check ([IO.File]::Exists($testColors) -and [IO.File]::Exists($testAudio)) 'Independent settings not saved'
 $saved=Get-Content $testAudio -Raw|ConvertFrom-Json
 Check ($saved.volume -eq 0.8 -and $saved.morseVolume -eq 0.23 -and $saved.noiseVolume -eq 0.67 -and $saved.noiseKind -eq 2 -and $saved.noisePlaybackOnly -eq 0 -and $saved.secondaryNoiseKind -eq 1 -and $saved.noiseSecondaryWhileReading -eq 1 -and $saved.secondaryNoiseVolume -eq 0.29 -and $saved.noisePalette -eq 2) 'Saved independent gain fields wrong'
 SetProp $sound MorseVolume ([double]1);SetProp $sound NoiseVolume ([double]1);SetProp $sound SecondaryNoiseKind 0;SetProp $sound NoiseSecondaryWhileReading $false
 Call $window LoadVolume
 Check ((Prop $sound MorseVolume) -eq 0.23 -and (Prop $sound NoiseVolume) -eq 0.67) 'Independent gains did not reload'
 Check ((Prop $sound SecondaryNoiseKind) -eq 1 -and (Prop $sound NoiseSecondaryWhileReading)) 'Secondary noise settings did not reload'
 Check ((Prop $sound SecondaryNoiseVolume) -eq 0.29) 'Secondary level did not reload'
 [IO.File]::WriteAllText($testAudio,'{"volume":0.31}')
 SetProp $sound MorseVolume ([double]1);SetProp $sound NoiseVolume ([double]1)
 Call $window LoadVolume
 Check ((Prop $sound Volume) -eq 0.31 -and (Prop $sound MorseVolume) -eq 1 -and (Prop $sound NoiseVolume) -eq 1) 'Legacy master-only preference migration failed'
 [IO.File]::WriteAllText($testAudio,'{"volume":0.31,"noiseKind":1,"secondaryNoiseKind":1,"noiseVolume":0.57}')
 Call $window LoadVolume
 Check ((Prop $sound NoiseKind) -eq 0 -and (Prop $sound SecondaryNoiseKind) -eq 0 -and (Prop $sound NoiseVolume) -eq 0.57) 'Old Dark settings were not mapped to new Dark index'
 Check ((Get-FileHash $chatColors).Hash -eq $chatHash) 'Bundled default colors changed'
 Check ((Get-FileHash $ownColors).Hash -eq $ownHash) 'Fixture changed production WordCraft colors'
 Check ($null -eq (Field (Field $sound noiseOutput) pump)) 'Settings test opened noise device'
 'PASS: independent volume changes/mutes/master composition/save/reload/legacy load; app-local colors and phase controls; palette saves only in fixture; Bundled defaults and app palettes unchanged. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
