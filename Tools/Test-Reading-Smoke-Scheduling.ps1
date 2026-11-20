$ErrorActionPreference='Stop'
$setup=Get-Content (Join-Path $PSScriptRoot 'Test-Smoke-Writer-Reader.ps1') -Raw
$setup=$setup.Replace("`r`n","`n")
$setup=$setup.Substring(0,$setup.IndexOf("try {`n `$grid.UpdateLayout()"))
$fixtureRoot=$PSScriptRoot;$setup=$setup.Replace('$PSScriptRoot','$fixtureRoot')
Invoke-Expression $setup
function SetField($o,$name,$value){$o.GetType().GetField($name,$flags).SetValue($o,$value)}
try {
 $grid.UpdateLayout();$editor=Field $window editor;$scene=Field $window scene
 Draw "WordCraft-Scheduling-$version.png"
 $editor.Text='A branching field carries the words gently across the screen.'
 Call $window ShowReader @($false,$true)
 $flow=Field $window flow;$reader=Prop $flow Reader
 $reader.PausePlayback();(Field $window timer).Stop()
 $before=Field $scene replyPrefix
 for($i=1;$i -le 500;$i++){Call $scene QueueReplyLetter @(0,('letters'+$i),'s')}
 Check ((Field $scene replyPrefix) -eq $before) 'Queuing performed immediate smoke work'
 Check ((Field $scene pendingReplyPrefix) -eq 'letters500') 'Latest-only buffer lost final state'
 $clock=Field $window clock
 SetField $window nextSmokeDeposit ($clock.Elapsed.TotalSeconds+10)
 Call $window FlushReadingSmoke
 Check ((Field $scene replyPrefix) -eq $before) 'Deposit bypassed cooldown'
 SetField $window nextSmokeDeposit 0.0
 Call $window FlushReadingSmoke
 Check ((Field $scene replyPrefix) -eq 'letters500') 'Scheduled flush lost latest letter'
 Check (!(Prop $scene ReplySmokePending)) 'Backlog remained after one flush'
 Check ((Field $window nextSmokeDeposit) -ge $clock.Elapsed.TotalSeconds+0.035) 'Cooldown did not start after work'
 foreach($prefix in 'slow1','slow2','slow3'){
  Start-Sleep -Milliseconds 80
  Call $scene QueueReplyLetter @(0,$prefix,'s');Call $window FlushReadingSmoke
  Check ((Field $scene replyPrefix) -eq $prefix) 'Slow input letter was dropped'
 }
 $shape=Call $scene BuildWord @('connected')
 for($i=0;$i -lt 50;$i++){
  Check ([object]::ReferenceEquals($shape,(Call $scene BuildWord @('connected')))) 'Repeated word outline was rebuilt'
 }
 Check $shape.IsFrozen 'Cached outline is mutable'
 $panel=Field $scene panel
 SetField $scene panel (New-Object Windows.Rect 0,0,($panel.Width+20),$panel.Height)
 $resized=Call $scene BuildWord @('connected')
 Check (![object]::ReferenceEquals($shape,$resized)) 'Resize reused stale outline'
 SetField $scene panel $panel
 $restored=Call $scene BuildWord @('connected')
 $tuning=$assembly.GetType('ViewTuning')
 $touch=$tuning.GetMethod('Touch',[Reflection.BindingFlags]'Public,Static')
 [void]$touch.Invoke($null,@())
 Check (![object]::ReferenceEquals($restored,(Call $scene BuildWord @('connected')))) 'Tuning reused stale outline'
 Call $window ShowEditor @(-1)
 Check (!(Prop $scene ReplySmokePending)) 'Cancelled reading leaked queued smoke'
 Call $scene BeginReply
 Call $scene TypeReplyLetter @(0,'compat','t')
 Check ((Field $scene replyPrefix) -eq 'compat') 'Existing immediate caller behavior changed'
 'PASS:500 rapid letters queue without GPU deposits; one latest state flushes, cooldown follows completion,80ms letters each apply, frozen outlines reuse/invalidate, return to typing clears work. Native offscreen/audio disabled.'
}finally{$hostWindow.Dispose();$window.Close()}
