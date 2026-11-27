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
        [McpServerTool(Name = "AuditPlcInputUsage"), Description("[L1][PLC-Software][READ-ONLY] Audit standard PLC input uses, caller access, FB/FC pins and direct GRAPH use, including software units. Safety program blocks are excluded. Requires a PLC software path and inclusive input ranges.")]
        public static ResponseJsonReport AuditPlcInputUsage(
            [Description("PLC Software object path from GetProjectTree")] string softwarePath,
            [Description("Inclusive ranges, e.g. [\"%I0.0..%I1.7\", \"%IW8..%ID12\"]; a single address is allowed")] string[] addressRanges,
            [Description("Exclude variables with a matching comment in any project language")] bool commentFilterEnabled = true,
            [Description("Case-insensitive comment substrings; default: [\"气缸\"]")] string[]? commentKeywords = null,
            [Description("Require the configured caller and read-only access, allowing verified direct InOut read/write")] bool fbRuleEnabled = true,
            [Description("Required block type (FB, FC or OB)")] string requiredBlockType = "FB",
            [Description("Required block number")] int requiredBlockNumber = 81,
            [Description("Required caller block programming language")] string requiredLanguage = "LAD",
            [Description("Require an Input or InOut connection to the configured FB/FC and disallow other FB/FC pin connections")] bool callPinRuleEnabled = false,
            [Description("Required called block type (FB or FC)")] string? requiredCallBlockType = null,
            [Description("Required called block number (positive integer)")] int? requiredCallBlockNumber = null,
            [Description("Required called block name")] string? requiredCallBlockName = null,
            [Description("Disallow direct input use in GRAPH transitions and permanent pre/post instructions")] bool graphTransitionRuleEnabled = false,
            [Description("Exclude standard caller FBs with any of these positive block numbers")] int[]? skipFbNumbers = null,
            [Description("Exclude standard caller FBs with any of these exact names, ignoring case")] string[]? skipFbNames = null)
        {
            try
            {
                var audit = new PlcInputUsageAudit(addressRanges, commentFilterEnabled, commentKeywords,
                    fbRuleEnabled, requiredBlockType, requiredBlockNumber, requiredLanguage,
                    callPinRuleEnabled, requiredCallBlockType, requiredCallBlockNumber, requiredCallBlockName, graphTransitionRuleEnabled,
                    skipFbNumbers, skipFbNames);
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
