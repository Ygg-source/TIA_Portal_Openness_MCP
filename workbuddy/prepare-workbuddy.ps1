[CmdletBinding(DefaultParameterSetName = 'Config')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Config')][string]$McpExePath,
    [Parameter(Mandatory, ParameterSetName = 'Package')][switch]$Package,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($Package) {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
    if ($output.TrimEnd('\') -eq $repository -or $output.StartsWith("$repository\", [StringComparison]::OrdinalIgnoreCase)) {
        throw '请将 WorkBuddy 分发包输出到 MCP 仓库之外。'
    }
}
[void][IO.Directory]::CreateDirectory($output)

if (-not $Package) {
    $exe = Get-Item -LiteralPath $McpExePath
    if ($exe -isnot [IO.FileInfo] -or $exe.Extension -ine '.exe') {
        throw 'McpExePath 必须指向本机的 MCP EXE 文件。'
    }
    $config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'mcp.json.template') | ConvertFrom-Json
    $config.mcpServers.'tia-plc-input-audit'.command = $exe.FullName
    $path = Join-Path $output 'mcp.json'
    $file = [IO.File]::Open($path, 'CreateNew', 'Write')
    $writer = [IO.StreamWriter]::new($file)
    try { $writer.WriteLine(($config | ConvertTo-Json -Depth 10)) }
    finally { $writer.Dispose() }
    $path
    return
}

$skillName = 'audit-plc-input-usage'
$sourceSkill = Join-Path $env:USERPROFILE ".agents/skills/$skillName"
if (-not (Test-Path -LiteralPath (Join-Path $sourceSkill 'SKILL.md') -PathType Leaf)) {
    throw "找不到 Codex 用户级 Skill。$sourceSkill"
}
$zip = Join-Path $output 'tia-plc-input-audit-workbuddy.zip'
if (Test-Path -LiteralPath $zip) { throw "分发包已存在，请另选输出目录。$zip" }
$stage = New-Item -ItemType Directory -Path (Join-Path $output ('workbuddy-' + [guid]::NewGuid().ToString('N')))
$skill = Join-Path $stage.FullName "skills/$skillName"
[void][IO.Directory]::CreateDirectory((Join-Path $skill 'config'))
[void][IO.Directory]::CreateDirectory((Join-Path $skill 'scripts'))

foreach ($file in 'README.md', 'connector-meta.json', 'icon.svg', 'mcp.json.template', 'prepare-workbuddy.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage.FullName
}
foreach ($file in 'README.md', 'config/audit-config.example.json', 'scripts/Read-AuditConfig.ps1', 'scripts/New-AuditReport.ps1') {
    Copy-Item -LiteralPath (Join-Path $sourceSkill $file) -Destination (Join-Path $skill $file)
}
$source = Get-Content -Raw -LiteralPath (Join-Path $sourceSkill 'SKILL.md')
$body = ($source -split '(?m)^---\r?$', 3)[2].TrimStart()
$frontMatter = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'skill-frontmatter.yaml')
[IO.File]::WriteAllText((Join-Path $skill 'SKILL.md'), $frontMatter.TrimEnd() + "`n`n" + $body)
Compress-Archive -LiteralPath $skill -DestinationPath (Join-Path $stage.FullName "$skillName.zip")
[IO.Compression.ZipFile]::CreateFromDirectory($stage.FullName, $zip)
# Remove only the staging directory created inside this output directory.
$resolvedStage = (Resolve-Path -LiteralPath $stage.FullName).ProviderPath
if ([IO.Path]::GetDirectoryName($resolvedStage) -eq (Resolve-Path -LiteralPath $output).ProviderPath) {
    Remove-Item -LiteralPath $resolvedStage -Recurse
}
$zip
