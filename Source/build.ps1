param(
    [string]$RimWorld = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
    [string]$Workshop = "C:\Program Files (x86)\Steam\steamapps\workshop\content\294100"
)
$managed = "$RimWorld\RimWorldWin64_Data\Managed"
$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\netstandard.dll",
    "$managed\Assembly-CSharp.dll", "$managed\UnityEngine.CoreModule.dll",
    "$Workshop\2009463077\Current\Assemblies\0Harmony.dll",
    "$Workshop\1635901197\1.6\Assemblies\FacialAnimation.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }
$out = Join-Path $PSScriptRoot "..\1.6\Assemblies\BionicIrises.dll"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /noconfig /nostdlib+ /target:library /optimize+ /warn:4 `
    "/out:$out" ($refs | ForEach-Object { "/reference:$_" }) (Join-Path $PSScriptRoot "BionicIrises.cs")
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $((Resolve-Path $out).Path)"
