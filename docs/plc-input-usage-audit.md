# V20 PLC I 点使用审计

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
  "requiredLanguage": "LAD"
}
```

`softwarePath` 和 `addressRanges` 必填。范围两端均包含，支持 `..` 或 `-` 分隔，单个地址也可用作一段。位、字节、字、双字分别占用 1、8、16、32 位。变量地址覆盖区间与任意范围有交集时纳入审计，多个范围重叠时每个变量仍只列一次。结构化输入标签使用原生交叉引用返回的地址跨度。绝对操作数按地址覆盖关联到变量，缺少完整覆盖时另列一条绝对地址记录。

任何项目语言的注释命中任一关键词时，变量状态为 `EXCLUDED`。关键词按不区分大小写的子串匹配。空注释正常参与检查。停用注释规则后仍读取注释。停用块规则后仍读取全部可用引用，`ruleStatus` 为 `NOT_EVALUATED`。

块规则按 Openness 块类型、`Number`、`ProgrammingLanguage` 以及原生访问类型判断。只有 `Read`、`ReadAndSymbol` 进入只读白名单。声明、定义及地址重叠关系等元数据不算程序使用。已确认的违规用法返回 `FAIL`。未知访问类型或无法确认的块属性影响完整性。

工具在 PLC 根目录与软件单元直接枚举变量表，因此不会受 `GetPlcTags` 的 5000 条返回上限影响。它通过 V20 的 `PlcTag.CrossReferenceService` 汇集符号引用，并导出代码块 XML 到系统临时目录扫描绝对操作数。LAD 与 FBD 主要使用连接与实参方向判别，SCL 使用 SimaticML 操作数和语句结构。未知连接、未知实参方向、不可读程序和不受支持的网络格式会影响完整性。原生交叉引用的定位粒度由 TIA 决定，通常是网络。XML 保留同一网络内能够区分的每个绝对操作数。XML 导出不会保存或编译工程。

## 结果

全量响应超过 MCP 会话阈值时，返回 `exportId`。在同一会话用 `GetExport` 从偏移量 0 开始获取所有字符切片，按 `nextOffset` 读到 `eof=true`，拼接后再解析 JSON。审计只执行一次。

- `data.projectName`、`plcName`、`plcModel`、`plcOrderNumber` 和 `softwarePath` 标识被审计的 PLC。CPU 型号和订货号为只读信息。
- `addressRanges` 和 `configuration` 记录有效配置。`totalTagCount` 是所有 PLC 变量数。`rangeTagCount` 和 `absoluteOnlyCount` 分别统计范围内的命名变量与独立绝对操作数。
- `excludedCount` 是注释排除数量。其余点按已确认使用、已确认未使用、通过、已证实违规、无法确认统计。已证实违规且覆盖不完整的点同时计入 `failCount` 和 `unconfirmedCount`。
- `points` 返回名称、地址、覆盖地址、变量表、软件单元、各语言注释、综合状态、两项规则状态及原因。每个引用含块路径、类型、编号、语言、访问类型、位置、来源和是否确认使用。
- `coverage`、`warnings` 和 `complete` 记录实际读取情况。覆盖不完整时 `overallStatus` 为 `INCOMPLETE`，即使已有部分点确认违规。完整扫描发现违规时为 `FAIL`。没有已使用的目标点时为 `UNUSED`。关闭块规则时完整结果为 `NOT_EVALUATED`。完整扫描且至少一个已使用的目标点全部合格时为 `PASS`。

[Siemens V20 STEP 7 交叉引用接口说明](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/getting-cross-references-for-step7)
