$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function Replay([bool]$repeat=$false){return (Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,([Windows.Input.ModifierKeys]::Control -bor [Windows.Input.ModifierKeys]::Shift),$repeat))}
try {
 $grid.UpdateLayout();$editor=Field $window editor
 Check ((Replay) -and $null -eq (Field $window flow) -and $editor.Text -eq '') 'Empty replay inserted text or eagerly opened reader'
 Check (!(Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,[Windows.Input.ModifierKeys]::None,$false))) 'Plain space was captured'
 Check (!(Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,[Windows.Input.ModifierKeys]::Shift,$false))) 'Shift+Space was captured'
 Check (!(Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,[Windows.Input.ModifierKeys]::Control,$false))) 'Ctrl+Space was captured'
 $editor.Text='CAPS';$editor.CaretIndex=4
 Check (!(Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,[Windows.Input.ModifierKeys]::Shift,$false)) -and !(Field $window reading)) 'Uppercase space started reading'
 $editor.SelectedText=' LOCK'
 Check ($editor.Text -eq 'CAPS LOCK' -and !(Field $window reading)) 'Uppercase phrase was interrupted'
 $editor.Text='First second third fourth fifth.';$original=$editor.Text
 Check (Replay) 'Ctrl+Shift+Space shortcut not handled'
 $flow=Field $window flow;$reader=Prop $flow Reader
 Check ((Field $window reading) -and $reader.PlaybackDirection -eq 1 -and $reader.TextAnchor -eq 0) 'Fresh typing replay did not create/start reader at beginning'
 $reader.SetSpeed(1);$reader.PausePlayback();$reader.Seek(13)
 $anchor=$reader.TextAnchor
 Check ((Replay $true) -and $reader.TextAnchor -eq $anchor -and $reader.PlaybackDirection -eq 0) 'Held key repeated or resumed playback'
 Check ((Replay) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 1) 'Paused replay did not rewind/play'
 $reader.PausePlayback();$reader.Seek($original.Length)
 Check ((Replay) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 1) 'Replay from end failed'
 Call $window ShowFullEditor;$editor.SelectAll()
 Check ((Replay) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 1 -and !$editor.IsHitTestVisible) 'Full-text replay did not return to reading'
 $reader.PausePlayback();$reader.Seek(13)
 Call $window BeginScrub
 Check ((Replay) -and !(Field $window scrubbing) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 1) 'Replay did not cancel scrub and restart'
 $fixture=Join-Path $project ('Bleeding-Edge/Validation/Replay-'+[guid]::NewGuid());New-Item -ItemType Directory -Path $fixture|Out-Null
 $scene=Field $window scene;$profiles=Field $scene Colors
 SetField $profiles FilePath (Join-Path $fixture 'Colors.json');SetField $window audioPath (Join-Path $fixture 'Audio.json')
 Call $window ToggleSettings
 $reader.PausePlayback();$reader.Seek(13)
 Check ((Replay) -and $null -ne (Field $window settings) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 1) 'Settings-open replay failed'
 Check ($editor.Text -eq $original) 'Replay changed source'
 Call $window CloseSettings
 $reader.PausePlayback();(Field $window timer).Stop()
 Check ($null -eq (Field (Field (Field $window sound) noiseOutput) pump)) 'Replay fixture opened audio output'
 'PASS: Ctrl+Shift+Space from empty/fresh typing/paused/end/full-text/settings, scrub cancellation, repeat suppression and unchanged source. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
