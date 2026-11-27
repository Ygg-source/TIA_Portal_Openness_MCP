using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public partial class McpServer
    {
        [McpServerTool(Name = "AuditPlcInputUsage"), Description("[L1][PLC-Software][READ-ONLY] Audit PLC input addresses in the open project, including software units. Reads multilingual tag comments and native references, and scans block XML for absolute operands. Returns per-input uses, rules, counts and completeness. Requires a PLC software path from GetProjectTree and inclusive input ranges.")]
        public static ResponseJsonReport AuditPlcInputUsage(
            [Description("PLC Software object path from GetProjectTree")] string softwarePath,
            [Description("Inclusive ranges, e.g. [\"%I0.0..%I1.7\", \"%IW8..%ID12\"]; a single address is allowed")] string[] addressRanges,
            [Description("Exclude variables with a matching comment in any project language")] bool commentFilterEnabled = true,
            [Description("Case-insensitive comment substrings; default: [\"气缸\"]")] string[]? commentKeywords = null,
            [Description("Require every executable reference to use the configured block and read-only access")] bool fbRuleEnabled = true,
            [Description("Required block type (FB, FC or OB)")] string requiredBlockType = "FB",
            [Description("Required block number")] int requiredBlockNumber = 81,
            [Description("Required block programming language")] string requiredLanguage = "LAD")
        {
            try
            {
                var audit = new PlcInputUsageAudit(addressRanges, commentFilterEnabled, commentKeywords,
                    fbRuleEnabled, requiredBlockType, requiredBlockNumber, requiredLanguage);
                var data = Portal.AuditPlcInputUsage(softwarePath, audit);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Data = data,
                    Message = $"PLC input audit: {data["overallStatus"]}.",
                    Meta = new System.Text.Json.Nodes.JsonObject
                    {
                        ["timestamp"] = DateTime.Now, ["success"] = true,
                        ["complete"] = data["complete"]!.GetValue<bool>(),
                    },
                };
            }
            catch (ArgumentException ex) { throw new McpException(ex.Message, McpErrorCode.InvalidParams); }
            catch (Exception ex) when (ex is not McpException) { throw EditError("AuditPlcInputUsage", softwarePath, ex); }
        }
    }
}
