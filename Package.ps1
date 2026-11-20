$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dist=Join-Path $root 'dist'
if(Test-Path $dist){throw 'dist exists; preserve previous artifacts before packaging.'}
$bundle=Join-Path $dist 'WordCraft-2.2.0-windows-x64'
New-Item -ItemType Directory -Path (Join-Path $bundle 'Bleeding-Edge/App') -Force|Out-Null
foreach($name in 'WordCraft.exe','WordCraftSmoke.dll'){
 Copy-Item -LiteralPath (Join-Path $root ('Bleeding-Edge/App/'+$name)) -Destination (Join-Path $bundle 'Bleeding-Edge/App')
}
Copy-Item -LiteralPath (Join-Path $root 'Bleeding-Edge/App/WordCraft.ico') -Destination (Join-Path $bundle 'Bleeding-Edge/App')
Get-ChildItem (Join-Path $root 'Defaults') -File|Copy-Item -Destination (Join-Path $bundle 'Bleeding-Edge')
foreach($name in 'LICENSE','README.md','RELEASE-NOTES.md','THIRD-PARTY-NOTICES.md','Open WordCraft.cmd'){
 Copy-Item -LiteralPath (Join-Path $root $name) -Destination $bundle
}
$zip=Join-Path $dist 'WordCraft-2.2.0-windows-x64.zip'
Compress-Archive -LiteralPath $bundle -DestinationPath $zip -CompressionLevel Optimal
$hash=(Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'),"$hash  WordCraft-2.2.0-windows-x64.zip`n")
Write-Output $zip
