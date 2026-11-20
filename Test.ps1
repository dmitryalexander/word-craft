$ErrorActionPreference='Stop'
foreach($name in 'Test-Quality','Test-Smoke-Writer-Reader','Test-Wind-Surface','Test-Character-Caret','Test-Replay-Shortcut','Test-Audio-Color-Settings','Test-Reading-End','Test-Ending-Colors','Test-Noise-Transition','Test-Reading-Smoke-Scheduling'){
 & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot ('Tools/'+$name+'.ps1'))
 if($LASTEXITCODE){throw "Failed: $name"}
}
