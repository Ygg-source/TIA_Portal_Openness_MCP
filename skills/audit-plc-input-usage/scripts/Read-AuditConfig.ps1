param(
    [string]$Path = (Join-Path $env:USERPROFILE '.agents/skills/audit-plc-input-usage/config/audit-config.json'),
    [switch]$Initialize
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$Path = [IO.Path]::GetFullPath($Path)

if ($Initialize) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $example = Join-Path $PSScriptRoot '../config/audit-config.example.json'
        $initial = Get-Content -Raw -LiteralPath $example | ConvertFrom-Json -AsHashtable
        $initial.scope.inputRanges = @()
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
        $file = [IO.File]::Open($Path, 'CreateNew', 'Write')
        $writer = [IO.StreamWriter]::new($file)
        try { $writer.WriteLine(($initial | ConvertTo-Json -Depth 10)) }
        finally { $writer.Dispose() }
    }
    $Path
    return
}

function InvalidField([string]$Field, [string]$Requirement) {
    throw "请修改 $Path 中的 $Field。$Requirement"
}

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "找不到配置 $Path。请先用 -Initialize 创建配置，填写检查范围后再运行。"
}
try { $config = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable -NoEnumerate }
catch { InvalidField '$' '请保存为有效的 JSON 文件。' }

if ($config.schemaVersion -isnot [long] -or $config.schemaVersion -ne 1) {
    InvalidField '$.schemaVersion' '填写整数 1。'
}
if ($config.plc.softwarePath -isnot [string]) {
    InvalidField '$.plc.softwarePath' '填写软件路径或空字符串。'
}
if ($config.scope.inputRanges -isnot [array] -or $config.scope.inputRanges.Count -eq 0) {
    InvalidField '$.scope.inputRanges' '请至少填写一段 I 点地址范围。'
}
$ranges = for ($index = 0; $index -lt $config.scope.inputRanges.Count; $index++) {
    $range = $config.scope.inputRanges[$index]
    if ($range -isnot [Collections.IDictionary]) {
        InvalidField ('$.scope.inputRanges[{0}]' -f $index) '填写含 start 和 end 的对象。'
    }
    foreach ($field in 'start', 'end') {
        if ($range[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($range[$field]) -or $range[$field] -match '\.\.|-') {
            InvalidField ('$.scope.inputRanges[{0}].{1}' -f $index, $field) '填写单个 I 点地址。'
        }
    }
    '{0}..{1}' -f $range.start.Trim(), $range.end.Trim()
}
$comment = $config.checks.commentExclusion
$block = $config.checks.fbReadOnly
$skipNumbers = @()
$skipNames = @()
if ($config.scope.Contains('skipFbBlocks')) {
    if ($config.scope.skipFbBlocks -isnot [array]) {
        InvalidField '$.scope.skipFbBlocks' '填写由正整数 FB 编号或非空 FB 名称组成的数组。'
    }
    for ($index = 0; $index -lt $config.scope.skipFbBlocks.Count; $index++) {
        $selector = $config.scope.skipFbBlocks[$index]
        if ($selector -is [long] -and $selector -gt 0 -and $selector -le [int]::MaxValue) {
            $skipNumbers += [int]$selector
        } elseif ($selector -is [string] -and -not [string]::IsNullOrWhiteSpace($selector)) {
            $skipNames += $selector.Trim()
        } else {
            InvalidField ('$.scope.skipFbBlocks[{0}]' -f $index) '填写大于零的 32 位 FB 编号或非空 FB 名称。'
        }
    }
}
if ($comment.enabled -isnot [bool]) { InvalidField '$.checks.commentExclusion.enabled' '填写 true 或 false。' }
if ($comment.keywords -isnot [array] -or @($comment.keywords.Where({ $_ -isnot [string] })).Count -ne 0) {
    InvalidField '$.checks.commentExclusion.keywords' '填写字符串数组。'
}
if ($block.enabled -isnot [bool]) { InvalidField '$.checks.fbReadOnly.enabled' '填写 true 或 false。' }
if ($block.targetBlockType -isnot [string] -or ($block.enabled -and $block.targetBlockType.Trim() -notin 'FB', 'FC', 'OB')) {
    InvalidField '$.checks.fbReadOnly.targetBlockType' '启用时填写 FB、FC 或 OB。'
}
if ($block.targetBlockNumber -isnot [long] -or $block.targetBlockNumber -lt [int]::MinValue -or
    $block.targetBlockNumber -gt [int]::MaxValue -or ($block.enabled -and $block.targetBlockNumber -lt 1)) {
    InvalidField '$.checks.fbReadOnly.targetBlockNumber' '填写 32 位整数，启用时须大于零。'
}
if ($block.targetLanguage -isnot [string] -or ($block.enabled -and [string]::IsNullOrWhiteSpace($block.targetLanguage))) {
    InvalidField '$.checks.fbReadOnly.targetLanguage' '启用时填写语言，例如 LAD。'
}
$outputDirectory = $config.report.outputDirectory
if ($outputDirectory -isnot [string] -or
    ($outputDirectory.Trim() -ne '' -and -not [IO.Path]::IsPathFullyQualified($outputDirectory.Trim()))) {
    InvalidField '$.report.outputDirectory' '填写本机的完整目录路径或空字符串。'
}
$prefix = $config.report.fileNamePrefix
if ($prefix -isnot [string] -or [string]::IsNullOrWhiteSpace($prefix) -or
    $prefix.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    InvalidField '$.report.fileNamePrefix' '填写有效的文件名前缀，不含路径。'
}

@{
    toolArguments = @{
        softwarePath = $config.plc.softwarePath.Trim()
        addressRanges = @($ranges)
        commentFilterEnabled = $comment.enabled
        commentKeywords = $comment.keywords
        fbRuleEnabled = $block.enabled
        requiredBlockType = $block.targetBlockType
        requiredBlockNumber = $block.targetBlockNumber
        requiredLanguage = $block.targetLanguage
        skipFbNumbers = $skipNumbers
        skipFbNames = $skipNames
    }
    report = @{
        outputDirectory = $outputDirectory.Trim()
        fileNamePrefix = $prefix.Trim()
    }
} | ConvertTo-Json -Depth 6
