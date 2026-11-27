# WorkBuddy 分发与安装

适用于能在 Windows 本地运行 WorkBuddy 和 stdio MCP 的用户。接收者需要安装 TIA Portal V20 和 PowerShell 7，开通 Siemens Openness，并准备包含 AuditPlcInputUsage 的 V20 MCP EXE 及其配套文件。连接前按本机 Openness V20 要求登记实际启动的 EXE。MCP 程序及 TIA 工程由接收者自行准备。

## 从分发包安装

1. 解压 tia-plc-input-audit-workbuddy.zip。保留其中的 audit-plc-input-usage.zip，用于导入 Skill。
2. 在 WorkBuddy 左侧打开“专家·技能·连接器”，选择“技能” → “添加技能” → “上传技能”，上传 **audit-plc-input-usage.zip**。确认已安装列表显示 TIA PLC I 点检查。
3. 在解压目录中打开 PowerShell 7。用自己的 MCP EXE 路径替换示例中的路径，再运行下列命令。把输出留在单独目录。

   ~~~powershell
   pwsh.exe -NoProfile -File .\prepare-workbuddy.ps1 -McpExePath 'D:\我的工具\TIA V20\TiaMcpServer.exe' -OutputDirectory .\local-mcp
   ~~~

4. 在“连接器”页面打开“自定义连接器”，按客户端提供的入口添加生成的 local-mcp\mcp.json 中的 tia-plc-input-audit。使用 JSON 编辑器时，保留原有 mcpServers 下的条目，只合入这个新条目，保存后确认已连接，并检查工具列表是否包含 AuditPlcInputUsage。若名称已存在，先核对现有条目指向的 EXE。
5. 在相同 PowerShell 中执行以下命令创建用户配置。已有配置会保留。如果你已经在 Codex 填写过配置，两端读取同一个文件。

   ~~~powershell
   pwsh.exe -NoProfile -File .\skills\audit-plc-input-usage\scripts\Read-AuditConfig.ps1 -Initialize
   ~~~

6. 按 [配置字段说明](skills/audit-plc-input-usage/README.md)填写实际地址范围。打开目标 TIA 工程，在 WorkBuddy 本地对话输入“使用 TIA PLC I 点检查技能，检查当前工程”。本地对话需能访问用户配置、执行 PowerShell 和连接本机 MCP。

脚本只生成输出目录中的新 mcp.json，遇到同名文件会停止。connector-meta.json 和图标用于描述连接器。此交付的部署方式是手动导入 Skill 并配置本地 MCP。外层 ZIP 是分发包，WorkBuddy 的技能上传入口使用内层 ZIP。云端 Agent 无法直接启动接收者 Windows 上的 EXE。

## 从仓库生成分发包

在仓库根目录运行：

~~~powershell
pwsh.exe -NoProfile -File .\workbuddy\prepare-workbuddy.ps1 -Package -OutputDirectory 'D:\分发'
~~~

生成脚本把 WorkBuddy 官方字段写入 Skill 元数据，正文、示例和脚本来自仓库的主 Skill。分发 ZIP 不包含用户配置、MCP EXE 或 TIA 工程。安装步骤依据 WorkBuddy 公开文档，客户端导入与工具调用以接收环境的验证结果为准。

- [WorkBuddy 技能元数据与 ZIP 结构](https://open.workbuddy.cn/docs/skill)
- [WorkBuddy 连接器元数据与 stdio 配置](https://open.workbuddy.cn/docs/connector)
- [WorkBuddy 客户端上传技能](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Skills-Market)
- [WorkBuddy 客户端添加自定义连接器](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Connector)
