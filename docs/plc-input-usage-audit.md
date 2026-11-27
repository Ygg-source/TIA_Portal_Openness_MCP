# V20 PLC I 点使用审计

使用 Codex 或 WorkBuddy 自动读取配置并生成中文 HTML 时，参阅 [PLC I 点检查 Skill](../skills/audit-plc-input-usage/README.md)。

`AuditPlcInputUsage` 是 lite Profile 的只读工具。打开单个工程后，从 `GetProjectTree` 取实际 PLC Software 路径。下面的例子检查两段 I 地址并应用默认规则。

```json
{
  "softwarePath": "PLC_1",
  "addressRanges": ["%I0.0..%I3.7", "%IW8..%ID12"],
  "commentFilterEnabled": true,
  "commentKeywords": ["气缸"],
  "fbRuleEnabled": true,
  "requiredBlockType": "FB",
  "requiredBlockNumber": 81,
  "requiredLanguage": "LAD",
  "callPinRuleEnabled": false,
  "graphTransitionRuleEnabled": false
}
```

`softwarePath` 和 `addressRanges` 必填。范围两端均包含，支持 `..` 或 `-` 分隔，单个地址也可用作一段。位、字节、字、双字分别占用 1、8、16、32 位。变量地址覆盖区间与任意范围有交集时纳入审计，多个范围重叠时每个变量仍只列一次。结构化输入标签使用原生交叉引用返回的地址跨度。绝对操作数按地址覆盖关联到变量，缺少完整覆盖时另列一条绝对地址记录。

任何项目语言的注释命中任一关键词时，变量状态为 `EXCLUDED`。关键词按不区分大小写的子串匹配。空注释正常参与检查。启用过滤时，无法读取注释会影响完整性。停用注释规则后仍读取注释。

## 检查规则

- 调用者访问规则由 `fbRuleEnabled` 控制，默认要求所有参与审计的普通程序引用出现在 LAD FB81。`Read`、`ReadAndSymbol` 以及可归因到已确认 `InOut` 直连的读写访问满足要求。如果原生写入可能来自该连接但无法核实归属，报告会注明无法确认。其他写入继续判违规，未知访问类型影响完整性。
- FB/FC 引脚规则由 `callPinRuleEnabled` 控制，默认关闭。启用时必须提供 `requiredCallBlockType`（`FB` 或 `FC`）、正整数 `requiredCallBlockNumber` 和非空 `requiredCallBlockName`。完整检查要求每点至少有一个目标块的 `Input` 或 `InOut` 直连。其他 FB/FC 的引脚以及 `Output`、`Return` 均判为违规。Contact 等普通读取位置仍适用调用者规则。
- GRAPH 直接使用规则由 `graphTransitionRuleEnabled` 控制，默认关闭。该字段覆盖转换条件、前固定指令和后固定指令。直接使用 I 点符号或绝对地址的操作数及调用实参均判为违规。步骤动作、联锁、监控仍适用已启用的调用者及引脚规则。

工具通过 `PlcTag.CrossReferenceService` 读取原生引用，并从标准代码块的 SimaticML 提取位置、操作数、调用及引脚信息。符号根据全局作用域、变量表身份和原生引用核对。FB 类型从 `CallInfo` 及块清单识别，通过实例 DB 的类型归属核对单实例，并用调用者接口的声明区分多重实例和参数实例。SCL 中 `=>` 确认输出连接，缺少方向时可由已识别的被调用块接口确认。SCL 复合表达式中的操作数记录为程序引用，只有作为独立实参使用时才计入引脚直连。声明、定义及地址重叠关系等元数据不计作程序使用。原生交叉引用的定位粒度由 TIA 决定，XML 保留同一网络内各可区分用法。不可读的普通块、无法识别的连接和不受支持的格式会显式降低覆盖完整性。

固定排除规则在导出和引用筛选前读取 Openness 的 F 编程语言枚举、安全软件单元归属及 `SafetyAdministration.RuntimeGroups`。运行组成员包括 F-OB、主安全块、主安全实例 DB 和运行组信息 DB。程序资源和编译器块等系统块组也纳入身份枚举。已确认的安全块跳过且不影响完整性。无法确认身份的块跳过并记录覆盖不足。CPU 型号和订货号仅用于显示。审计在系统临时目录处理普通块 XML，不编译或保存工程。直接枚举 PLC 根目录和普通软件单元的变量表，不受 `GetPlcTags` 5000 条返回上限影响。

## 结果

可在用户 JSON 的 `scope.skipFbBlocks` 填写编号和名称的混合数组，例如 `[81, 120, "Feedback_Check"]`。读取脚本将它映射成 MCP 的 `skipFbNumbers` 和 `skipFbNames`。匹配编号或完整名称（忽略大小写）的普通 FB 将从调用者扫描和用法统计中排除。其他块调用这些 FB 且调用位置缺少引脚方向时，会按需导出接口元数据以确认该外部用法。

全量响应超过 MCP 会话阈值时，返回 `exportId`。在同一会话用 `GetExport` 从偏移量 0 开始获取所有字符切片，按 `nextOffset` 读到 `eof=true`，拼接后再解析 JSON。审计只执行一次。

- `data.projectName`、`plcName`、`plcModel`、`plcOrderNumber` 和 `softwarePath` 标识被审计的 PLC。CPU 型号和订货号为只读信息。
- `addressRanges` 和 `configuration` 记录有效配置。`totalTagCount` 是所有 PLC 变量数。`rangeTagCount` 和 `absoluteOnlyCount` 分别统计范围内的命名变量与独立绝对操作数。
- `checkedInputCount = passCount + failCount + unconfirmedCount + notEvaluatedCount`。每个已使用或无法确认是否使用的审计点计一次。注释排除、确认未使用和确认仅在安全程序使用分别计入 `excludedCount`、`unusedCount` 和 `safetyOnlyExcludedCount`。仅在安全程序使用的明细保留旧状态 `UNUSED`，以 `safetyOnlyExcluded: true` 区分。尚未确认普通程序覆盖时，已发现安全引用的点仍可能计入检测总数。
- 新版 `unconfirmedCount` 只统计综合状态为 `INCOMPLETE` 的点。已确认违规且存在覆盖不足的点计入 `failCount` 和 `partiallyUnconfirmedCount`。需要原有重叠口径的直接调用方可读取 `legacyUnconfirmedCount`。按规则分别违反的点数见 `ruleViolationCounts`。安全块总跳过数见 `safetySkippedBlockCount`，导出尝试数见 `coverage.xmlExportAttempts` 和 `coverage.safetyXmlExportAttempts`。
- `points` 返回变量身份、注释、`references`、`callPinConnections`、`graphLocations`、`violations`、`reasons` 和 `complete`。`callerRuleStatus` 亦保留为旧字段 `ruleStatus`。其余状态分别见 `callPinRuleStatus` 和 `graphDirectUseStatus`。引脚连接包含调用者、网络、被调用块、实例、引脚、方向及确认状态。GRAPH 位置包含区域、网络、转换名和编号（适用时）、输入点及确认状态。
- `uncertaintyReasons` 单列影响确认的原因。引用的 `inOutWriteAllowed` 标记按 `InOut` 允许的访问。`coverage.unreadableBlocks`、`coverage.incompleteBlocks` 和 `coverage.skippedFbBlocks` 返回对应的块名称、类型和编号，前两项附带失败原因或网络解析问题。
- 已确认违规的点和总体结果优先返回 `FAIL`，同时保留 `complete: false`、`coverage` 和 `warnings`。其余结果在不完整时为 `INCOMPLETE`。全部程序规则关闭且检查完整时为 `NOT_EVALUATED`。规则开启且确认没有普通程序用法时为 `UNUSED`。完整满足启用的规则时为 `PASS`。

[Siemens V20 STEP 7 交叉引用接口说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7)
