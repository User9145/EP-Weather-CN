# shortname.ps1 - print the 8.3 short NAME (leaf only) for each path given as
# argument; print NONE when the path has no short name. Used by install.bat to
# build the file:// URL that fits the 50-char URL slot in ep_weather_host.dll.
$t = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class S8 {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern uint GetShortPathNameW(string l, StringBuilder s, uint n);
}
'@
Add-Type -TypeDefinition $t
foreach ($p in $args) {
  $sb = New-Object Text.StringBuilder 512
  $r = [S8]::GetShortPathNameW($p, $sb, 512)
  $s = $sb.ToString()
  if ($r -gt 0 -and $s -ne $p -and $s -match '~') {
    Write-Output ($s.TrimEnd('\').Split('\')[-1])
  } else {
    Write-Output "NONE"
  }
}
