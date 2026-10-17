# Start-together test: A hosts the planet from the main menu (no quick test), B joins from the main menu.
# Drive both with harness commands: lobbyopen / lobbyready / hostquick / pickscen / earlydone / status ...
$g = "D:\SteamLibrary\steamapps\common\RimWorld"
$launcher = "$env:LOCALAPPDATA\RimFactions\Launcher\RimFactionsLauncher.exe"
$testDir = "$env:LOCALAPPDATA\RimFactions\test"
New-Item -ItemType Directory -Force $testDir | Out-Null
Remove-Item "$testDir\A.log", "$testDir\B.log", "$testDir\*.cmd" -ErrorAction SilentlyContinue
Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--host", "--savedatafolder", "`"$env:USERPROFILE\rimworld-test-C`"", "--no-wait", "--extra", "`"-rimfactions-test=A -username=PlayerA`""
Start-Sleep 100
Start-Process $launcher -ArgumentList "--game", "`"$g`"", "--join", "tcp:127.0.0.1:30602", "--savedatafolder", "`"$env:USERPROFILE\rimworld-test-D`"", "--no-wait", "--extra", "`"-rimfactions-test=B -username=PlayerB`""
