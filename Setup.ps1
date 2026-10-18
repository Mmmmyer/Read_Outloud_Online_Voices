# Read Aloud setup - copies the app to Documents\Read Aloud, adds a Desktop shortcut, and starts it.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$src  = $PSScriptRoot
$dest = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Read Aloud'

# Close a running copy so its files can be replaced
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*ReadAloud.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

New-Item -ItemType Directory -Force $dest | Out-Null
if ($src -ne $dest) {
    foreach ($f in 'ReadAloud.ps1', 'ReadAloud.cs', 'ReadAloud.ico') { Copy-Item (Join-Path $src $f) $dest -Force }
}
# Remove the "downloaded from the internet" mark so Windows doesn't block the files
Get-ChildItem $dest | Unblock-File

$ws  = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Read Aloud.lnk'))
$lnk.TargetPath       = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$lnk.Arguments        = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -STA -File `"$dest\ReadAloud.ps1`""
$lnk.IconLocation     = "$dest\ReadAloud.ico"
$lnk.WorkingDirectory = $dest
$lnk.WindowStyle      = 7
$lnk.Description      = 'Highlight text and press Ctrl+Alt+R to hear it'
$lnk.Save()

Start-Process $lnk.FullName
[System.Windows.Forms.MessageBox]::Show(
    "Read Aloud is installed in:`n$dest`n`nA 'Read Aloud' shortcut is on your Desktop and the app is now running (speaker icon in the system tray).`n`nHighlight text and press Ctrl+Alt+R to hear it.`nRight-click the tray icon for all shortcuts, voices, and 'Run at startup'.",
    'Read Aloud installed') | Out-Null
