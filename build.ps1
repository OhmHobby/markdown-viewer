<#
  Builds Markdown Viewer into .\dist\MarkdownViewer and zips it as .\dist\MarkdownViewer-<version>-win-x64.zip.

  Needs only what ships with Windows 10/11: Windows PowerShell, tar, and the .NET Framework C# compiler.
  Pinned dependencies are downloaded once into .\build\cache:
    marked   (Markdown parser, MIT)          from registry.npmjs.org
    KaTeX    (math rendering, MIT)           from registry.npmjs.org
    WebView2 SDK (Microsoft, BSD-style)      from nuget.org

  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$MarkedVersion = '18.0.14'
$KatexVersion = '0.18.9'
$WebView2Version = '1.0.4191.47'

$root = $PSScriptRoot
$cache = Join-Path $root 'build\cache'
$dist = Join-Path $root 'dist\MarkdownViewer'
$version = [regex]::Match((Get-Content -Raw "$root\src\MarkdownViewer.cs"), 'Version = "([^"]+)"').Groups[1].Value

function Get-Package([string]$name, [string]$url) {
    $dir = Join-Path $cache $name
    if (-not (Test-Path $dir)) {
        Write-Host "Downloading $name"
        $archive = "$dir.download"
        Invoke-WebRequest -UseBasicParsing $url -OutFile $archive
        New-Item -ItemType Directory -Force $dir | Out-Null
        tar -xf $archive -C $dir          # tar.exe reads both .tgz (npm) and .zip (NuGet)
        if ($LASTEXITCODE) { throw "Could not unpack $name" }
        Remove-Item $archive
    }
    $dir
}

New-Item -ItemType Directory -Force $cache | Out-Null
$marked = Get-Package "marked-$MarkedVersion" "https://registry.npmjs.org/marked/-/marked-$MarkedVersion.tgz"
$katex = Get-Package "katex-$KatexVersion" "https://registry.npmjs.org/katex/-/katex-$KatexVersion.tgz"
$webview2 = Get-Package "webview2-$WebView2Version" "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$WebView2Version/microsoft.web.webview2.$WebView2Version.nupkg"

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force "$dist\katex\fonts", "$dist\licenses" | Out-Null

# The viewer page, with the Markdown parser inlined as its first script.
$parser = (Get-Content -Raw -Encoding UTF8 "$marked\package\lib\marked.umd.js") -replace '(?m)^//# sourceMappingURL=.*$', ''
$page = (Get-Content -Raw -Encoding UTF8 "$root\src\md.html").Replace('/*@@MARKED@@*/', $parser.Trim())
[IO.File]::WriteAllText("$dist\md.html", $page, (New-Object Text.UTF8Encoding $false))

# KaTeX: script, stylesheet and the woff2 fonts (the only format current engines ask for).
Copy-Item "$katex\package\dist\katex.min.js", "$katex\package\dist\katex.min.css" "$dist\katex"
Copy-Item "$katex\package\dist\fonts\*.woff2" "$dist\katex\fonts"

# WebView2: the .NET wrappers and the native loader.
Copy-Item "$webview2\lib\net462\Microsoft.Web.WebView2.Core.dll", "$webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll" $dist
Copy-Item "$webview2\runtimes\win-x64\native\WebView2Loader.dll" $dist

Copy-Item "$root\LICENSE" "$dist\licenses\MarkdownViewer.txt"
Copy-Item "$marked\package\LICENSE" "$dist\licenses\marked.txt"
Copy-Item "$katex\package\LICENSE" "$dist\licenses\KaTeX.txt"
Copy-Item "$webview2\LICENSE.txt" "$dist\licenses\WebView2.txt"
Copy-Item "$root\assets\file.ico", "$root\Install.cmd" $dist

# The app itself, compiled with the C# compiler that is part of the .NET Framework.
$csc = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$dist\MarkdownViewer.exe" "/win32icon:$root\assets\app.ico" `
    "/r:$webview2\lib\net462\Microsoft.Web.WebView2.Core.dll" "/r:$webview2\lib\net462\Microsoft.Web.WebView2.WinForms.dll" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /r:System.Core.dll "$root\src\MarkdownViewer.cs"
if ($LASTEXITCODE) { throw 'Compiling MarkdownViewer.exe failed' }

$zip = Join-Path $root "dist\MarkdownViewer-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Entry names use "/" (Windows PowerShell's ZipFile.CreateFromDirectory would write "\", which other unzip tools mangle).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($file in Get-ChildItem -Recurse -File $dist) {
        $entry = 'MarkdownViewer/' + $file.FullName.Substring($dist.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, 'Optimal')
    }
} finally { $archive.Dispose() }

$size = (Get-ChildItem -Recurse -File $dist | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Built Markdown Viewer {0}: {1} ({2:N1} MB)" -f $version, $dist, $size)
Write-Host "Zip: $zip"
