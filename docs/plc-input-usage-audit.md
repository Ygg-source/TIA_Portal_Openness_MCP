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

- 调用者访问规则由 `fbRuleEnabled` 控制，默认要求所有参与审计的普通程序引用出现在 LAD FB81。同一块中的 STL 网络按 STL 判断。`Read`、`ReadAndSymbol` 以及可归因到已确认 `InOut` 直连的读写访问满足访问要求。如果原生写入可能来自该连接但无法核实归属，报告会注明无法确认。其他写入继续判违规，未知访问类型影响完整性。
- FB/FC 引脚规则由 `callPinRuleEnabled` 控制，默认关闭。启用时必须提供 `requiredCallBlockType`（`FB` 或 `FC`）、正整数 `requiredCallBlockNumber` 和非空 `requiredCallBlockName`。完整检查要求每点至少有一个目标块的 `Input` 或 `InOut` 直连。其他 FB/FC 的引脚以及 `Output`、`Return` 均判为违规。Contact 等普通读取位置仍适用调用者规则。
- GRAPH 直接使用规则由 `graphTransitionRuleEnabled` 控制，默认关闭。该字段覆盖转换条件、前固定指令和后固定指令。直接使用 I 点符号或绝对地址的操作数及调用实参均判为违规。步骤动作、联锁、监控仍适用已启用的调用者及引脚规则。

工具通过 `PlcTag.CrossReferenceService` 读取原生引用，并从标准代码块的 SimaticML 提取位置、操作数、调用及引脚信息。符号根据全局作用域、变量表身份和原生引用核对。FB 类型从 `CallInfo` 及块清单识别，通过实例 DB 的类型归属核对单实例，并用调用者接口的声明区分多重实例和参数实例。SCL 中 `=>` 确认输出连接，缺少方向时可由已识别的被调用块接口确认。SCL 复合表达式中的操作数记录为程序引用，只有作为独立实参使用时才计入引脚直连。声明、定义及地址重叠关系等元数据不计作程序使用。原生交叉引用的定位粒度由 TIA 决定，XML 保留同一网络内各可区分用法。不可读的普通块、无法识别的连接和不受支持的格式会显式降低覆盖完整性。

审计在导出前排除已确认的 Know-how protected 块、ProDiag FB、ProDiag OB、系统块组中的块和安全程序。保护状态读取失败会降低覆盖完整性并阻止导出。ProDiag 由 V20 的 `ProDiag` / `ProDiag_OB` 语言枚举及 FB / OB 对象类型共同识别。`S7_PDIAG` 继续按普通程序处理。系统块组只用于身份筛选。安全分类沿用 F 编程语言枚举、安全软件单元归属和 `SafetyAdministration.RuntimeGroups`。运行组成员包括 F-OB、主安全块、主安全实例 DB 和运行组信息 DB。F-CPU 上的普通块仍参与检查。

原生交叉引用中的工艺对象通过 V20 的 `UnderlyingObject` 和对象父级识别，包括 `TechnologicalInstanceDB`、工艺参数及工艺对象组。工艺对象引用从明细中排除，混合引用保留普通程序的位置。仅有工艺对象引用的输入不构成普通程序用法。其他无法识别的引用仍计入覆盖不足。审计在系统临时目录处理普通块 XML，工程操作限于读取。变量表枚举覆盖 PLC 根目录和普通软件单元。

## STL 解析

V20 的 STL 网络通过 `SW.Blocks.CompileUnit/AttributeList/NetworkSource` 下的 `StatementList` 读取。PA40080 的 LAD 块里实际导出了 `StatementList/v5` 网络，网络的 `ProgrammingLanguage` 为 `STL`，每条指令对应一个 `StlStatement`，助记符位于 `StlToken/@Text`，行内注释位于 `LineComment`。SCL 继续使用 `StructuredText` 解析。

STL 按指令和操作数位置区分读取、写入和读写。例如 `A`、`AN`、`O`、`ON` 和 `L` 读取，`Assign`（`=`）、`S`、`R` 和 `T` 写入，边沿指令读写其边沿存储位。`SET` 将 RLO 置 1，`SAVE` 将 RLO 保存到 BR，两条指令都没有 I 点操作数。地址和全局符号来自结构化操作数，注释和字符串中的地址文本不产生引用。 [Siemens V20 SET](https://docs.tia.siemens.cloud/r/en-us/v20/stl-s7-1500/stl-mnemonic-s7-1500/bit-logic-operations-s7-1500/set-set-rlo-to-1-s7-1500)、[Siemens V20 SAVE](https://docs.tia.siemens.cloud/r/en-us/v20/stl-s7-1500/stl-mnemonic-s7-1500/bit-logic-operations-s7-1500/save-save-rlo-in-br-bit-s7-1500)

STL 的 FB/FC 调用沿用 `callPinConnections`。从 `CallInfo` 获取目标、实例和实参，结合块清单及可读接口补充身份与方向。受保护的被调用块只使用调用处已有的引脚方向及块清单身份。间接寻址、动态索引、指针，以及无法识别的操作数、指令或网络结构会降低覆盖完整性。

## 结果

可在用户 JSON 的 `scope.skipFbBlocks` 填写编号和名称的混合数组，例如 `[81, 120, "Feedback_Check"]`。读取脚本将它映射成 MCP 的 `skipFbNumbers` 和 `skipFbNames`。匹配编号或完整名称（忽略大小写）的普通 FB 将从调用者扫描和用法统计中排除。其他块调用这些可读 FB 且调用位置缺少引脚方向时，会按需导出接口元数据以确认该外部用法。

全量响应超过 MCP 会话阈值时，返回 `exportId`。在同一会话用 `GetExport` 从偏移量 0 开始获取所有字符切片，按 `nextOffset` 读到 `eof=true`，拼接后再解析 JSON。审计只执行一次。

- `data.projectName`、`plcName`、`plcModel`、`plcOrderNumber` 和 `softwarePath` 标识被审计的 PLC。CPU 型号和订货号为只读信息。
- `addressRanges` 和 `configuration` 记录有效配置。`totalTagCount` 是所有 PLC 变量数。`rangeTagCount` 和 `absoluteOnlyCount` 分别统计范围内的命名变量与独立绝对操作数。
- `checkedInputCount = passCount + failCount + unconfirmedCount + notEvaluatedCount`。每个已使用或无法确认是否使用的审计点计一次。注释排除、确认未使用和确认仅在安全程序使用分别计入 `excludedCount`、`unusedCount` 和 `safetyOnlyExcludedCount`。仅在安全程序使用的明细保留旧状态 `UNUSED`，以 `safetyOnlyExcluded: true` 区分。尚未确认普通程序覆盖时，已发现安全引用的点仍可能计入检测总数。
- 新版 `unconfirmedCount` 只统计综合状态为 `INCOMPLETE` 的点。已确认违规且存在覆盖不足的点计入 `failCount` 和 `partiallyUnconfirmedCount`。需要原有重叠口径的直接调用方可读取 `legacyUnconfirmedCount`。按规则分别违反的点数见 `ruleViolationCounts`。安全块总跳过数见 `safetySkippedBlockCount`，导出尝试数见 `coverage.xmlExportAttempts` 和 `coverage.safetyXmlExportAttempts`。
- `points` 返回变量身份、注释、`references`、`callPinConnections`、`graphLocations`、`violations`、`reasons` 和 `complete`。`callerRuleStatus` 亦保留为旧字段 `ruleStatus`。其余状态分别见 `callPinRuleStatus` 和 `graphDirectUseStatus`。引脚连接包含调用者、网络、被调用块、实例、引脚、方向及确认状态。GRAPH 位置包含区域、网络、转换名和编号（适用时）、输入点及确认状态。
- `uncertaintyReasons` 单列影响确认的原因。引用的 `inOutWriteAllowed` 标记按 `InOut` 允许的访问。`coverage.unreadableBlocks`、`coverage.incompleteBlocks` 和 `coverage.skippedFbBlocks` 返回对应的块名称、类型和编号，前两项附带失败原因或网络解析问题。
- 已确认违规的点和总体结果优先返回 `FAIL`，同时保留 `complete: false`、`coverage` 和 `warnings`。其余结果在不完整时为 `INCOMPLETE`。全部程序规则关闭且检查完整时为 `NOT_EVALUATED`。规则开启且确认没有普通程序用法时为 `UNUSED`。完整满足启用的规则时为 `PASS`。

[Siemens V20 STEP 7 交叉引用接口说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7)
