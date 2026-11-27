---
name: audit-plc-input-usage
description: 用 TIA Portal V20 MCP 检查当前打开的单个工程中的 PLC I 点使用情况，按配置执行注释排除和指定块只读检查，并输出中文 HTML。适用于 I 点审计、输入点使用检查和 FB81 只读检查。
---

# 检查 PLC I 点使用情况

使用 Windows 本地执行端。相对脚本路径以本 SKILL.md 所在目录为准。

1. 调用 TIA MCP 的 Bootstrap 核对服务可用和 TIA 版本为 20。核对工具列表中有 AuditPlcInputUsage。工具未直接列出时，用 FindTools 按名称发现，再通过 CallTool 调用已发现的工具。如果仍找不到，停止并提示换成包含该工具的 V20 MCP 版本。
2. 运行下列命令。脚本读取用户级配置，验证字段，并返回 toolArguments 和 report。若配置不存在，先用同一脚本加 -Initialize 创建空范围配置。实际范围为空或配置校验失败时，提示修改脚本指出的文件和字段，并停止。

   ~~~powershell
   pwsh.exe -NoProfile -File "<技能目录>\scripts\Read-AuditConfig.ps1"
   ~~~
3. 若 Bootstrap 显示已有工程绑定，使用该工程。未绑定时，调用 ListPortalProcessProjects 确认已打开的工程。只有一个目标时，用 AttachToOpenProject 绑定该工程。没有打开工程时，请用户先打开。目标有歧义时，请用户明确当前工程。保持已绑定工程及所有打开的工程原状。禁止自行打开、切换、关闭、编译或修改工程。通过 GetProjectTree 读取工程及 PLC 软件的完整路径。PLC 路径已配置时，要求与树中路径匹配。未配置时自动使用唯一 PLC 软件。有多个时列出实际路径并询问一次，等用户选择后继续。无 PLC 或配置路径找不到时停止并说明原因。
4. 将所选完整路径填入 toolArguments.softwarePath，按脚本生成的其余参数调用一次 AuditPlcInputUsage。交叉引用和规则结论以该工具为准。调用失败时说明阻塞，禁止通过阅读梯形图替代检查。
5. 将完整 JSON 响应原样交给报告脚本。优先用结构化结果或工具返回的原始 JSON 文本。若响应带有 meta.exportId，在同一 MCP 会话调用 SaveExport，传入该编号、临时目录下的新文件完整路径和 raw=true。截断结果不得出报告。没有 SaveExport 时，从偏移 0 用 GetExport 按 nextOffset 读取到 eof=true，将 message 切片原样拼接并核对字符总长。直接响应也写入临时文件。不要在命令文本中插入来自结果的值或注释，临时文件只用于本次转换。
6. 用实际路径运行：

   ~~~powershell
   pwsh.exe -NoProfile -File "<技能目录>\scripts\New-AuditReport.ps1" -ResultPath "<临时 JSON 路径>"
   ~~~

   报告脚本读取 complete、warnings、overallStatus 和原始统计。处理完成后删除本次创建的临时 JSON 文件。向用户给出报告路径、已使用、合格、不合格、无法确认、排除的数量，并说明覆盖不完整或存在检查提示的情况。摘要和附件只交付中文 HTML 及其统计。
