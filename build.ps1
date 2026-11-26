$ErrorActionPreference = "Stop"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:winexe /platform:x64 /main:RightPanel.Program /optimize+ /out:RightPanel.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll Ccd.cs CcdTiming.cs Engine.cs UI.cs
    if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}
