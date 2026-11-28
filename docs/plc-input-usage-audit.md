# V20 PLC I 点使用审计

使用 Codex 自动读取配置并生成中文 HTML 时，参阅用户级 Skill 文档 %USERPROFILE%\.agents\skills\audit-plc-input-usage\README.md。WorkBuddy 分发包从同一目录生成。 [WorkBuddy 分发说明](../workbuddy/README.md)

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

确认未使用的变量直接计入 `unusedCount`。对其余变量，启用 `commentFilterEnabled` 时，任何项目语言的注释命中关键词都会将变量标为 `EXCLUDED`。关键词按不区分大小写的子串匹配。关闭该选项只停用关键词排除。

## 检查规则

- 缺失注释是固定规则。原生交叉引用或 SimaticML 确认有普通程序用法，且成功读取的各语言注释均为空白时，点状态为 `FAIL`，`violations` 返回 `rule: "comment"`、`code: "MISSING_INPUT_COMMENT"` 和消息“I 点缺少注释。”。任一语言有非空白文本即可满足规则。注释读取失败时，`commentStatus` 为 `INCOMPLETE`。无变量表归属的绝对地址没有项目变量注释可读，`commentStatus` 为 `NOT_EVALUATED`。
- 调用者访问规则由 `fbRuleEnabled` 控制，默认要求所有参与审计的普通程序引用出现在 LAD FB81。同一块中的 STL 网络按 STL 判断。`Read`、`ReadAndSymbol` 以及可归因到已确认 `InOut` 直连的读写访问满足访问要求。如果原生写入可能来自该连接但无法核实归属，报告会注明无法确认。其他写入继续判违规，未知访问类型影响完整性。
- FB/FC 引脚规则由 `callPinRuleEnabled` 控制，默认关闭。启用时必须提供 `requiredCallBlockType`（`FB` 或 `FC`）、正整数 `requiredCallBlockNumber` 和非空 `requiredCallBlockName`。完整检查要求每点至少有一个目标块的 `Input` 或 `InOut` 连接。支持实参直连，也支持经可核实的 LAD 常开或常闭触点链连接。其他 FB/FC 的引脚以及 `Output`、`Return` 均判为违规。触点操作数按 `Read` 检查调用者访问。
- GRAPH 直接使用规则由 `graphTransitionRuleEnabled` 控制，默认关闭。该字段覆盖转换条件、前固定指令和后固定指令。直接使用 I 点符号或绝对地址的操作数及调用实参均判为违规。V20 FlgNet 的数组变量下标使用外层操作数的连线核实位置。下标按读取记录，属于当前 GRAPH 区域，外层操作数的调用引脚仍归外层。确认位于审计范围外的下标变量不会降低 I 点覆盖。步骤动作、联锁、监控仍适用已启用的调用者及引脚规则。

传入非空 `allowedUsages` 数组可组合多组调用者 A 和被调用 FB/FC B。每组由原有形状的 `fbReadOnly` 和 `callPinTarget` 对象组成，替代直接调用的单组参数。A 按类型、编号及语言匹配。B 按类型及编号或名称匹配，同时填写编号和名称时两者都须匹配。两段的 `enabled` 分别控制本组的只读访问要求和 Input/InOut 方向要求，A/B 身份和引脚连接仍须核实。单行完整满足任意一组即可通过组合检查。跨组返回 `WRONG_CALL_PAIR`，已确认没有 FB/FC 引脚连接返回 `MISSING_PAIRED_CALL_PIN`。无法确认的用法保留不完整状态。GRAPH 独立检查。用户配置示例见 Skill README。

工具通过 `PlcTag.CrossReferenceService` 读取原生引用，并从标准代码块的 SimaticML 提取位置、操作数、调用及引脚信息。符号根据全局作用域、变量表身份和原生引用核对。FB 类型从 `CallInfo` 及块清单识别，通过实例 DB 的类型归属核对单实例，并用调用者接口的声明区分多重实例和参数实例。SCL 中 `=>` 确认输出连接，缺少方向时可由已识别的被调用块接口确认。SCL 复合表达式中的操作数记录为程序引用，只有作为独立实参使用时才计入引脚直连。声明、定义及地址重叠关系等元数据不计作程序使用。原生交叉引用的定位粒度由 TIA 决定，XML 保留同一网络内各可区分用法。不可读的普通块、无法识别的连接和不受支持的格式会显式降低覆盖完整性。

LAD 从 `IdentCon` 定位触点的 `operand`，沿触点 `out` 的 Wire 到下一触点 `in` 或 `CallInfo` 中声明的参数。TIA V20 的常闭触点可导出为带 `Negated Name="operand"` 的 `Contact`，解析器也识别 `NegatedContact`。允许明确的单源分支，每条路径可串联多个触点。遇到边沿触点、其它逻辑元件或分支合并时停止该路径。显示结果沿用 `callPinConnections`，接线存在冲突或端口身份不明确时降低对应点的引脚检查完整性。

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
- `addressRanges` 和 `configuration` 记录有效配置，包括多组模式的 `configuration.allowedUsages`。`totalTagCount` 是所有 PLC 变量数。`rangeTagCount` 和 `absoluteOnlyCount` 分别统计范围内的命名变量与独立绝对操作数。
- `checkedInputCount = passCount + failCount + unconfirmedCount + notEvaluatedCount`。每个参与检查的点计一次。只有原生引用、SimaticML、符号、地址和适用区域均扫描完整，且无普通程序用法和潜在用法时，才计入 `unusedCount`，跳过所有规则和 `points` 明细。确认仅有安全引用的点也计入 `unusedCount`，其子集数量见 `safetyOnlyExcludedCount`。关键词排除单独计入 `excludedCount`。
- `unconfirmedCount` 统计综合状态为 `INCOMPLETE` 的点。已确认违规且存在覆盖不足的点计入 `failCount` 和 `partiallyUnconfirmedCount`。需要原有重叠口径的直接调用方可读取 `legacyUnconfirmedCount`。按规则分别违反的点数见 `ruleViolationCounts`，其中 `comment` 统计缺失注释的点。安全块总跳过数见 `safetySkippedBlockCount`，导出尝试数见 `coverage.xmlExportAttempts` 和 `coverage.safetyXmlExportAttempts`。
- `points` 返回变量身份、注释、`references`、`callPinConnections`、`graphLocations`、`violations`、`reasons` 和 `complete`。`table` 是变量表枚举时获取的完整路径，例如 `Sensors/Inputs`、`Software units/UnitA/Sensors/Inputs`。根表返回表名，无表归属的绝对地址返回空值。`callerRuleStatus` 亦保留为旧字段 `ruleStatus`。其余状态分别见 `commentStatus`、`callPinRuleStatus` 和 `graphDirectUseStatus`。引脚连接包含调用者、网络、被调用块、实例、引脚、方向及确认状态。GRAPH 位置包含区域、网络、转换名和编号（适用时）。前后固定指令的序号见 `graphOperationNumber`。
- 每个解析位置和每个实际引脚都在 `references[].assessment` 或 `references[].callPins[].assessment` 中提供对应的规则状态、综合状态、完整性和原因。显示引脚行时取该引脚的判定。旧参数模式下，已确认没有引脚连接的位置，其行级引脚状态为 `NOT_EVALUATED`，仍按整个点检查是否缺少目标输入连接。调用者违规按 `field` 分别给出 `blockType`、`blockNumber`、`language` 的 `expected` 和 `actual`，继续使用 `WRONG_CALLER` 代码。多组模式下，无匹配调用者时用差异最少的一组说明各字段差异。GRAPH 违规使用 `graphRegion` 区分三种位置。
- 调用者 A 优先取 TIA 交叉引用，被调用 FB/FC、引脚和方向取 SimaticML。`references` 将已确认属于同一块、网络、符号和访问类型的交叉引用摘要归并到各 XML 位置。V20 中文交叉引用只给出前后永久指令及序号时，以同一块、区域、序号、符号和访问类型核对 SimaticML。匹配到同一导出网络的已确认操作数时保留实际网络及指令序号。无法可靠对应的交叉引用仍保留。`origin` 和 `callerOrigin` 分别标识位置及调用者身份的来源。不同 XML 位置和各实际引脚连接均保留，检查规则继续使用原证据。
- `uncertaintyReasons` 单列影响确认的原因。引用的 `inOutWriteAllowed` 标记按 `InOut` 允许的访问。`coverage.unreadableBlocks`、`coverage.incompleteBlocks` 和 `coverage.skippedFbBlocks` 返回对应的块名称、类型和编号，前两项附带程序文件夹路径及失败原因或网络解析问题。`unreadableBlocks` 也包含按需补读被调用块接口时的失败记录。`unreadableBlocks[].failureStage` 将导出失败标为 `export`，将导出后的读取或处理失败标为 `scan`。`reasonCode` 将可识别的 TIA 工程一致性拒绝标为 `inconsistent`。导出失败造成覆盖不完整时，HTML 显示块名称、编号、程序文件夹路径及库关联修复提示。导出后的读取或解析失败另列块身份及对应原因。
- 已确认违规的点和总体结果优先返回 `FAIL`，覆盖不足时同时保留 `complete: false`、`coverage` 和 `warnings`。无已确认违规但检查不完整时为 `INCOMPLETE`。扫描完整且没有参与检查的点时，总体状态为 `UNUSED`。只有无变量表归属的绝对地址参与检查且可选规则均关闭时为 `NOT_EVALUATED`。完整满足适用规则时为 `PASS`。

[Siemens V20 STEP 7 交叉引用接口说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7)
