param([Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
$outputPath=[IO.Path]::GetFullPath($OutputFile)
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'integrations/vscode'
$manifest=Get-Content -LiteralPath (Join-Path $source 'package.json') -Raw | ConvertFrom-Json
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
$temp=$outputPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
try {
    $zip=[IO.Compression.ZipFile]::Open($temp,[IO.Compression.ZipArchiveMode]::Create)
    try {
        function AddText([string]$name,[string]$value) {
            $entry=$zip.CreateEntry($name)
            $stream=$entry.Open()
            try {$bytes=[Text.UTF8Encoding]::new($false).GetBytes($value);$stream.Write($bytes,0,$bytes.Length)} finally {$stream.Dispose()}
        }
        AddText '[Content_Types].xml' '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="json" ContentType="application/json"/><Default Extension="js" ContentType="application/javascript"/><Default Extension="vsixmanifest" ContentType="text/xml"/></Types>'
        AddText 'extension.vsixmanifest' ('<?xml version="1.0"?><PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"><Metadata><Identity Language="en-US" Id="mason-workspace" Version="'+$manifest.version+'" Publisher="mason-agent"/><DisplayName>Mason Agent Workspace</DisplayName><Description>Share local editor selections with Mason.</Description><Properties><Property Id="Microsoft.VisualStudio.Code.Engine" Value="^1.85.0"/></Properties></Metadata><Installation><InstallationTarget Id="Microsoft.VisualStudio.Code"/></Installation><Dependencies/><Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/></Assets></PackageManifest>')
        foreach($item in 'extension.js','package.json'){AddText ('extension/'+$item) ([IO.File]::ReadAllText((Join-Path $source $item)))}
    } finally {$zip.Dispose()}
    [IO.File]::Move($temp,$outputPath,$true)
} finally {if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
