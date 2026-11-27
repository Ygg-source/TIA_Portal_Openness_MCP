param(
    [Parameter(Mandatory)][string]$ResultPath,
    [string]$ConfigPath = (Join-Path $env:USERPROFILE '.agents/skills/audit-plc-input-usage/config/audit-config.json')
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$settings = (& (Join-Path $PSScriptRoot 'Read-AuditConfig.ps1') -Path $ConfigPath | ConvertFrom-Json).report
$result = Get-Content -Raw -LiteralPath $ResultPath | ConvertFrom-Json -AsHashtable -NoEnumerate
if ($result.meta.truncated -or $result.meta.exportId -or $result.ok -eq $false) {
    throw '未取得完整的 PLC 检查响应，无法输出报告。'
}
$data = if ($null -ne $result.data) { $result.data } else { $result }

$countLabels = [ordered]@{
    usedCount = '已使用 I 点'
    passCount = '合格'
    failCount = '不合格'
    unconfirmedCount = '无法确认'
    excludedCount = '排除'
}
foreach ($key in $countLabels.Keys) {
    if ($data[$key] -isnot [long] -or $data[$key] -lt 0) { throw 'PLC 检查响应缺少有效统计。' }
}
if ($data.complete -isnot [bool] -or $data.configuration.fbRuleEnabled -isnot [bool] -or
    $data.configuration.commentFilterEnabled -isnot [bool] -or $data.warnings -isnot [array] -or
    $data.addressRanges -isnot [array] -or $data.addressRanges.Count -eq 0 -or $data.points -isnot [array]) {
    throw 'PLC 检查响应缺少完整结果。'
}

function Html([string]$Value) {
    [Net.WebUtility]::HtmlEncode($Value)
}

$blockEnabled = $data.configuration.fbRuleEnabled
$summary = switch -CaseSensitive ($data.overallStatus) {
    'PASS'          { '已检查的 I 点均符合要求。' }
    'FAIL'          { '发现不符合要求的 I 点。' }
    'INCOMPLETE'    { '部分检查未完成，无法确认整体结果。' }
    'UNUSED'        { '参与检查的范围内没有已使用的 I 点。' }
    'NOT_EVALUATED' { '已统计 I 点使用情况。指定块只读检查未启用。' }
    default         { throw 'PLC 检查响应的总体结果无法识别。' }
}
if (-not $data.complete) { $summary = '部分检查未完成，无法确认整体结果。' }
elseif (-not $blockEnabled -and $data.overallStatus -eq 'PASS') { $summary = '已统计 I 点使用情况。指定块只读检查未启用。' }
if ($data.warnings.Count -gt 0) {
    if ($data.complete -and $blockEnabled -and $data.overallStatus -eq 'PASS') { $summary = '检查有提示，请核实本次覆盖范围。' }
    else { $summary += ' 检查有提示，请核实本次覆盖范围。' }
}

$blockLabel = if ($blockEnabled) {
    $language = switch ($data.configuration.requiredLanguage) {
        'LAD' { '梯形图' }
        'FBD' { '功能块图' }
        'SCL' { '结构化控制语言' }
        'STL' { '语句表' }
        default { $data.configuration.requiredLanguage }
    }
    '指定块访问检查已启用（{0}{1}，{2}），确认的 InOut 直连允许读写。' -f $data.configuration.requiredBlockType, $data.configuration.requiredBlockNumber, $language
} else { '指定块只读检查未启用。' }
$commentLabel = if ($data.configuration.commentFilterEnabled) { '注释排除已启用。' } else { '注释排除未启用。' }
$rangeLabel = Html ($data.addressRanges -join '、')
$model = if ($data.plcModel) { Html $data.plcModel } else { '未读取到' }
$order = if ($data.plcOrderNumber) { Html $data.plcOrderNumber } else { '未读取到' }
$stats = foreach ($key in $countLabels.Keys) {
    '<div class="stat"><dt>{0}</dt><dd>{1}</dd></div>' -f $countLabels[$key], $data[$key]
}

$directory = $settings.outputDirectory
if (-not $directory) {
    $directory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'TIA-PLC-Input-Audit-Reports'
}
[void][IO.Directory]::CreateDirectory($directory)
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fffffff'
$path = Join-Path $directory ('{0}_{1}.html' -f $settings.fileNamePrefix, $stamp)

# Render the server's classifications and counts. Known reason formats supply only user-facing categories.
$rows = [Text.StringBuilder]::new()
foreach ($point in $data.points) {
    if ($point.status -ceq 'UNUSED') { continue }
    $label, $color = switch -CaseSensitive ($point.status) {
        'EXCLUDED'      { '已排除'; 'gray' }
        'INCOMPLETE'    { '无法确认'; 'yellow' }
        'NOT_EVALUATED' { '未启用'; 'gray' }
        'FAIL'          { if ($blockEnabled) { '不合格'; 'red' } else { '未启用'; 'gray' } }
        'PASS'          { if ($blockEnabled) { '合格'; 'green' } else { '未启用'; 'gray' } }
        default         { throw 'PLC 检查响应含无法识别的单点结果。' }
    }
    $detail = if ($color -eq 'red') {
        $issues = @(switch -Regex ($point.reasons) {
            ': expected .+, found .*\.$' { '使用的程序块类型、编号或编程语言与设置不符。' }
            ': (Write|WriteAndSymbol|RW|ReadWriteAndSymbol) is not read-only\.$' { '发现写入用法。' }
        }) | Select-Object -Unique
        if ($issues) { $issues -join ' ' } else { '存在不符合指定块只读要求的用法。' }
    } else { '—' }
    if (-not $point.complete) {
        $uncertainty = if ($point.Contains('uncertaintyReasons')) { $point.uncertaintyReasons } else { $point.reasons }
        $reasonText = if ($uncertainty) { $uncertainty -join [Environment]::NewLine } else { '检查覆盖不完整。' }
        $detail = if ($detail -eq '—') { $reasonText } else { $detail + [Environment]::NewLine + $reasonText }
    }
    $comment = @(
        foreach ($entry in $point.comment.GetEnumerator()) {
            if ($entry.Key -cne '_error' -and $entry.Value -is [string] -and $entry.Value.Trim()) { $entry.Value }
        }
    ) | Select-Object -Unique
    $name = if ($point.name) { Html $point.name } else { '未命名' }
    $commentText = if ($comment) { Html ($comment -join [Environment]::NewLine) } else { '—' }
    [void]$rows.AppendLine(('<tr><td>{0}</td><td>{1}</td><td>{2}</td><td><span class="{3}">{4}</span></td><td>{5}</td></tr>' -f
        $name, (Html $point.address), $commentText, $color, $label, (Html $detail)))
}
if ($rows.Length -eq 0) { [void]$rows.AppendLine('<tr><td colspan="5">无符合明细展示条件的 I 点。</td></tr>') }

$coverageLabels = [ordered]@{
    unreadableBlocks = '无法读取的块'
    incompleteBlocks = '解析不完整的块及网络格式'
    skippedFbBlocks = '按配置跳过的 FB'
}
$coverageHtml = foreach ($key in $coverageLabels.Keys) {
    if (-not $data.coverage -or -not $data.coverage[$key]) { continue }
    $blocks = $data.coverage[$key]
    $blockRows = foreach ($item in ($blocks | Sort-Object blockType, blockNumber, blockName)) {
        $blockId = '{0}{1}' -f $item.blockType, $item.blockNumber
        $reason = if ($item.issues) { $item.issues -join [Environment]::NewLine } else { $item.reason }
        '<tr><td>{0}</td><td>{1}</td><td>{2}</td></tr>' -f (Html $blockId), (Html $item.blockName), (Html $reason)
    }
    '<h2>{0}（{1}）</h2><div class="table"><table><thead><tr><th>块编号</th><th>块名称</th><th>原因</th></tr></thead><tbody>{2}</tbody></table></div>' -f
        $coverageLabels[$key], $blocks.Count, ($blockRows -join [Environment]::NewLine)
}

$html = @"
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>PLC I 点使用情况检查</title>
<style>
body{margin:0;color:#172333;background:#fff;font:15px/1.45 "Microsoft YaHei",system-ui,sans-serif}
main{width:min(96%,1200px);margin:1.2rem auto}h1{font-size:1.5rem;margin:0 0 .55rem}
p{margin:.35rem 0;overflow-wrap:anywhere}dl{display:flex;flex-wrap:wrap;gap:.5rem;margin:.65rem 0}
.stat{min-width:6rem;padding:.3rem .55rem;background:#f0f3f6;border-radius:.25rem}
dd{margin:0;font-weight:700;font-size:1.15rem}.table{overflow-x:auto}
table{width:100%;min-width:36rem;border-collapse:collapse;table-layout:fixed}
th,td{text-align:left;vertical-align:top;border:1px solid #d9e0e7;padding:.33rem .45rem;overflow-wrap:anywhere}
th{background:#eef2f6}td{white-space:pre-wrap}th:nth-child(1){width:21%}th:nth-child(2){width:12%}
th:nth-child(3){width:29%}th:nth-child(4){width:12%}
span{display:inline-block;padding:.04rem .32rem;border-radius:.2rem}
.green{color:#126238;background:#def4e6}.red{color:#931f22;background:#fde7e7}
.yellow{color:#704b02;background:#fff0bd}.gray{color:#475461;background:#edf0f2}
</style>
</head>
<body><main>
<h1>PLC I 点使用情况检查</h1>
<p><strong>PLC 型号</strong> $model　<strong>订货号</strong> $order</p>
<p><strong>检查范围</strong> $rangeLabel</p>
<p><strong>结果</strong> $(Html $summary)</p>
<p>$(Html $commentLabel) $(Html $blockLabel)</p>
<dl>$($stats -join [Environment]::NewLine)</dl>
<p>颜色说明　<span class="green">符合要求</span> <span class="red">不符合要求</span>
<span class="yellow">无法确认</span> <span class="gray">未启用或已排除</span></p>
<div class="table"><table aria-label="I 点检查明细">
<thead><tr><th>名称</th><th>地址</th><th>注释</th><th>结果</th><th>原因</th></tr></thead>
<tbody>
$rows</tbody></table></div>
$($coverageHtml -join [Environment]::NewLine)
</main></body></html>
"@
$file = [IO.File]::Open($path, 'CreateNew', 'Write')
$writer = [IO.StreamWriter]::new($file)
try { $writer.WriteLine($html) }
finally { $writer.Dispose() }

$summaryData = [ordered]@{ path = $path; summary = $summary }
foreach ($key in $countLabels.Keys) { $summaryData[$key] = $data[$key] }
$summaryData | ConvertTo-Json
