# TIA PLC I 点使用情况检查

用于 Windows 本机当前打开的一个 TIA Portal V20 工程。需要 TIA Portal V20、可用的 Siemens Openness 和包含 AuditPlcInputUsage 的 V20 MCP。配置与报告脚本使用 PowerShell 7，无需额外安装模块。CPU 型号和订货号仅在报告中显示。

## 安装到 Codex

从 MCP 仓库根目录打开 PowerShell 7，执行：

~~~powershell
$skill = Join-Path $env:USERPROFILE '.agents\skills\audit-plc-input-usage'
New-Item -ItemType Directory -Path $skill -Force | Out-Null
Copy-Item -LiteralPath '.\skills\audit-plc-input-usage\SKILL.md', '.\skills\audit-plc-input-usage\README.md', '.\skills\audit-plc-input-usage\scripts' -Destination $skill -Recurse -Force
$configDir = New-Item -ItemType Directory -Path (Join-Path $skill 'config') -Force
Copy-Item -LiteralPath '.\skills\audit-plc-input-usage\config\audit-config.example.json' -Destination $configDir.FullName -Force
pwsh.exe -NoProfile -File (Join-Path $skill 'scripts\Read-AuditConfig.ps1') -Initialize
~~~

仓库中的本目录为维护来源。更新 Skill 时重新复制这些文件。初始化命令保留已有配置。首次创建的检查范围为空，需要填写后才能运行。

仓库示例填写了 `%I4000.0` 至 `%I4500.0`、注释排除关键词 `气缸`、`备用`、`预留` 和本机报告目录。初始化会保留示例的关键词和报告目录，请按本机使用情况填写地址范围并核对目录。

## 编辑检查配置

在资源管理器的地址栏输入以下路径，用记事本打开文件，修改后以 UTF-8 保存。

~~~
%USERPROFILE%\.agents\skills\audit-plc-input-usage\config\audit-config.json
~~~

下表的美元符号代表整个文件。点号后面是下一层字段名，方括号代表数组中的第几项，从 0 开始。布尔值 true 和 false 不加引号，数字也不加引号。字符串用双引号。

| JSON 字段 | 填写位置 | 作用和修改方法 |
| --- | --- | --- |
| schemaVersion | $.schemaVersion | 配置的格式版本。保留数字 1。 |
| softwarePath | $.plc.softwarePath | TIA 工程树里的完整 PLC 软件路径。有一套 PLC 时留空即可。有多套时可先让 Codex 或 WorkBuddy 列出路径，再复制目标路径到引号中。留空时会询问你选哪一套。 |
| inputRanges | $.scope.inputRanges | 要检查的 I 地址范围列表。每段写一对花括号，多个范围用逗号隔开，最后一段后面不加逗号。空数组会停止检查。 |
| start | $.scope.inputRanges[n].start | 第 n 段的起始地址，例如 "%I0.0"。 |
| end | $.scope.inputRanges[n].end | 第 n 段的结束地址，例如 "%I15.7"。两端均计入。只查单个地址时两端填写相同地址。 |
| 注释启用 | $.checks.commentExclusion.enabled | true 时按关键词排除 I 点。改为 false 后停止排除，原本会被排除的点也参加使用检查，报告仍显示注释。 |
| 注释关键词 | $.checks.commentExclusion.keywords | 填字符串数组，例如 ["气缸", "备用"]。任一项目语言的注释包含任一关键词即排除，匹配时忽略大小写。[] 表示没有关键词。 |
| FB 检查启用 | $.checks.fbReadOnly.enabled | true 时检查每处用法的块类型、编号、语言和只读要求。改为 false 后继续统计 I 点使用情况和注释排除，指定块规则显示未启用，合格和不合格数均为零。 |
| FB 类型 | $.checks.fbReadOnly.targetBlockType | 启用时在引号中填 "FB"、"FC" 或 "OB"。默认 "FB"。 |
| FB 编号 | $.checks.fbReadOnly.targetBlockNumber | 启用时填写正整数。默认 81。 |
| 编程语言 | $.checks.fbReadOnly.targetLanguage | 启用时填写 TIA 的块语言，例如 "LAD"。默认 "LAD"。 |
| 报告目录 | $.report.outputDirectory | 留空时写到 Windows 的“文档”目录下的 TIA-PLC-Input-Audit-Reports。自定义时填写完整路径并使用双反斜杠，例如 "D:\\Reports\\TIA"。也可以写 "D:/Reports/TIA"。 |
| 文件名前缀 | $.report.fileNamePrefix | 填有效的文件名文本，例如 "PLC_I点使用情况检查"。不用写目录或扩展名，脚本会加时间戳和 .html。 |

例如，找到 scope 里的 inputRanges，把空数组改成下面两段。范围可以交叠，同一点由审计工具去重。

~~~json
"inputRanges": [
  { "start": "%I0.0", "end": "%I15.7" },
  { "start": "%I32.0", "end": "%I47.7" }
]
~~~

位地址示例为 %I0.0。也可填写审计工具支持的字节、字和双字地址，例如 %IB0、%IW8、%ID12。地址合同见仓库的《V20 PLC I 点使用审计》。

## 在 Codex 使用

1. 在 TIA Portal V20 中打开要检查的工程。
2. 确认 Codex 可发现 V20 MCP 的 AuditPlcInputUsage。使用新发布或新构建产物时，先按本仓库的 V20 Openness 白名单约定核对实际运行的 EXE。刚更改 MCP 配置或安装 Skill 时，在新会话验证工具和技能是否出现。
3. 对 Codex 说“检查当前 TIA 工程的 PLC I 点使用情况”。也可显式选择 $audit-plc-input-usage。
4. 如提示有多套 PLC，选择给出的其中一条路径。结束后打开答复中的 HTML 文件。

已使用 I 点统计沿用审计工具口径，已排除的点单独统计。已确认违规但仍有部分用法无法确认的点可同时计入不合格和无法确认。明细列出参与检查的已使用点、无法确认点和已排除点。覆盖不完整时，报告会保留已确认的违规，并说明总体结果无法确认。

## 分发给 WorkBuddy

使用同次交付的 WorkBuddy ZIP，解压后按其中的 README 安装。仓库中可从 workbuddy/README.md 查阅生成分发包的命令。两端使用同一份用户级配置及同一套报告脚本。
