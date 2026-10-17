# Captures one RimWorld window (by process id) to a PNG. Only that window's rectangle is captured.
param([int]$ProcessId, [string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RFWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@
$p = Get-Process -Id $ProcessId
$h = $p.MainWindowHandle
$r = New-Object RFWin+RECT
for ($i = 0; $i -lt 10; $i++) {
  [RFWin]::ShowWindow($h, 9) | Out-Null
  [RFWin]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 1500
  [RFWin]::GetWindowRect($h, [ref]$r) | Out-Null
  if (($r.R - $r.L) -gt 800) { break }
}
Start-Sleep -Milliseconds 800
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap $w, $hgt
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [RFWin]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
if (-not $ok) { $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size) }
$g.Dispose()
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out ($w x $hgt)"

