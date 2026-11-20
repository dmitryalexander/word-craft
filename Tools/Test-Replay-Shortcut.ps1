$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
function Replay([bool]$repeat=$false){return (Call $window HandleReplayShortcut @([Windows.Input.Key]::Space,([Windows.Input.ModifierKeys]::Control -bor [Windows.Input.ModifierKeys]::Shift),$repeat))}
function PausedStart([bool]$repeat=$false){return (Call $window HandleTabShortcut @([Windows.Input.Key]::Tab,[Windows.Input.ModifierKeys]::Control,$repeat))}
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
 Call $window ShowEditor @([int]-1);$editor.CaretIndex=13
 Check ((PausedStart) -and (Field $window reading) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'Typing Ctrl+Tab did not start paused at beginning'
 $reader.Seek(13)
 Check ((PausedStart $true) -and $reader.TextAnchor -eq 13) 'Repeated Ctrl+Tab reset position'
 Check ((PausedStart) -and (Field $window reading) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'Paused reading Ctrl+Tab returned to writing or kept old position'
 $reader.Seek(13);$reader.StartPlayback(1)
 Check ((PausedStart) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'Playing Ctrl+Tab did not rewind and stop'
 $reader.Seek($original.Length)
 Check ((PausedStart) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'End Ctrl+Tab did not rewind paused'
 Call $window ShowFullEditor;$editor.Select(4,6)
 Check ((PausedStart) -and (Field $window reading) -and !$editor.IsHitTestVisible -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'Full mesh Ctrl+Tab failed'
 Call $window BeginScrub
 Check ((PausedStart) -and !(Field $window scrubbing) -and $reader.TextAnchor -eq 0 -and $reader.PlaybackDirection -eq 0) 'Ctrl+Tab did not cancel scrub'
 Check ($editor.Text -eq $original) 'Ctrl+Tab changed source'
 Call $window HandleTabShortcut @([Windows.Input.Key]::Tab,[Windows.Input.ModifierKeys]::None,$false)|Out-Null
 Check (!(Field $window reading)) 'Plain Tab no longer returns to writing'
 Call $window HandleTabShortcut @([Windows.Input.Key]::Tab,[Windows.Input.ModifierKeys]::None,$false)|Out-Null
 Check ((Field $window reading) -and $reader.PlaybackDirection -eq 1) 'Plain Tab no longer starts reading'
 $reader.PausePlayback();(Field $window timer).Stop()
 Check ($null -eq (Field (Field (Field $window sound) noiseOutput) pump)) 'Replay fixture opened audio output'
 'PASS: Ctrl+Tab rewinds paused from writing/mesh/playing/paused/end, repeat and gesture guards, plain Tab unchanged; Ctrl+Shift+Space from empty/fresh typing/paused/end/full-text/settings, scrub cancellation, repeat suppression and unchanged source. Offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
