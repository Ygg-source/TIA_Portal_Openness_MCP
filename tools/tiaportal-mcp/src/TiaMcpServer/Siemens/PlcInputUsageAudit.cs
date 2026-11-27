using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    internal readonly struct PlcInputSpan
    {
        private static readonly Regex AddressPattern = new Regex(
            @"^%?I(?:(?:X)?(?<byte>[0-9]+)\s*\.\s*(?<bit>[0-7])|(?<size>[BWD])\s*(?<byte>[0-9]+))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal PlcInputSpan(long start, long end) { Start = start; End = end; }
        internal long Start { get; }
        internal long End { get; }
        internal bool Overlaps(PlcInputSpan other) => Start <= other.End && other.Start <= End;
        internal bool Contains(PlcInputSpan other) => Start <= other.Start && End >= other.End;

        internal static bool TryParse(string? address, out PlcInputSpan span)
        {
            span = default;
            if (address == null) return false;
            var match = AddressPattern.Match(address.Trim());
            if (!match.Success || !int.TryParse(match.Groups["byte"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var offset)) return false;
            var start = offset * 8L;
            var size = match.Groups["size"].Value.ToUpperInvariant();
            var width = size == "D" ? 32 : size == "W" ? 16 : size == "B" ? 8 : 1;
            if (match.Groups["bit"].Success) start += match.Groups["bit"].Value[0] - '0';
            span = new PlcInputSpan(start, start + width - 1);
            return true;
        }

        internal static bool TryRange(string? range, out PlcInputSpan span)
        {
            span = default;
            var ends = range?.Split(new[] { "..", "-" }, StringSplitOptions.None);
            if (ends == null || ends.Length < 1 || ends.Length > 2 || !TryParse(ends[0], out var first))
                return false;
            if (ends.Length == 1) { span = first; return true; }
            if (!TryParse(ends[1], out var last) || first.Start > last.Start || first.End > last.End)
                return false;
            span = new PlcInputSpan(first.Start, last.End);
            return true;
        }

        internal string BitAddress => FormattableString.Invariant($"%I{Start / 8}.{Start % 8}");
        internal string Canonical
        {
            get
            {
                var width = End - Start + 1;
                if (width == 1) return BitAddress;
                if (Start % 8 == 0 && (width == 8 || width == 16 || width == 32))
                    return FormattableString.Invariant($"%I{(width == 8 ? "B" : width == 16 ? "W" : "D")}{Start / 8}");
                return BitAddress + ".." + FormattableString.Invariant($"%I{End / 8}.{End % 8}");
            }
        }
    }

    internal enum PlcInputProgramKind
    {
        Unknown,
        Standard,
        Safety,
    }

    internal sealed class PlcInputCallPin
    {
        internal string? BlockType { get; set; }
        internal int? BlockNumber { get; set; }
        internal string? BlockName { get; set; }
        internal string? InstanceScope { get; set; }
        internal string? InstanceName { get; set; }
        internal string? InstanceKind { get; set; }
        internal string? InstanceTypeName { get; set; }
        internal string? CallUid { get; set; }
        internal string? Pin { get; set; }
        internal string? Direction { get; set; }
        internal bool Confirmed => BlockNumber.HasValue && !string.IsNullOrWhiteSpace(BlockName)
            && (BlockType is "FB" or "FC") && !string.IsNullOrWhiteSpace(Pin)
            && (Direction is "Input" or "InOut" or "Output" or "Return");

        internal JsonObject ToJson() => new JsonObject
        {
            ["blockType"] = BlockType, ["blockNumber"] = BlockNumber, ["blockName"] = BlockName,
            ["instanceScope"] = InstanceScope, ["instanceName"] = InstanceName, ["callUid"] = CallUid,
            ["instanceKind"] = InstanceKind, ["instanceTypeName"] = InstanceTypeName,
            ["pin"] = Pin, ["direction"] = Direction, ["confirmed"] = Confirmed,
        };
    }

    internal sealed class PlcInputAuditReference
    {
        internal string? BlockPath { get; set; }
        internal string? BlockType { get; set; }
        internal int? BlockNumber { get; set; }
        internal string? Language { get; set; }
        internal string? Access { get; set; }
        internal bool AccessComplete { get; set; } = true;
        internal string? Location { get; set; }
        internal string? Detail { get; set; }
        internal string? Address { get; set; }
        internal string Origin { get; set; } = "crossReference";
        internal bool Absolute { get; set; }
        internal bool ConfirmedUsage { get; set; } = true;
        internal int? Network { get; set; }
        internal string? BlockName { get; set; }
        internal string? Symbol { get; set; }
        internal string? GraphRegion { get; set; }
        internal string? TransitionName { get; set; }
        internal int? TransitionNumber { get; set; }
        internal int? GraphSequence { get; set; }
        internal List<PlcInputCallPin> CallPins { get; } = new List<PlcInputCallPin>();

        internal JsonObject ToJson() => new JsonObject
        {
            ["blockPath"] = BlockPath, ["blockType"] = BlockType, ["blockNumber"] = BlockNumber,
            ["language"] = Language, ["access"] = Access, ["location"] = Location, ["detail"] = Detail,
            ["accessComplete"] = AccessComplete && (PlcInputUsageAudit.IsRead(Access) || PlcInputUsageAudit.IsWrite(Access)),
            ["address"] = Address, ["origin"] = Origin, ["absolute"] = Absolute, ["confirmedUsage"] = ConfirmedUsage,
            ["network"] = Network, ["blockName"] = BlockName, ["symbol"] = Symbol, ["graphRegion"] = GraphRegion,
            ["transitionName"] = TransitionName, ["transitionNumber"] = TransitionNumber, ["graphSequence"] = GraphSequence,
            ["callPins"] = new JsonArray(CallPins.Select(p => (JsonNode)p.ToJson()).ToArray()),
        };
    }

    internal sealed class PlcInputAuditPoint
    {
        internal string? Name { get; set; }
        internal string? Table { get; set; }
        internal string? SoftwareUnit { get; set; }
        internal string Address { get; set; } = "";
        internal PlcInputSpan Span { get; set; }
        internal JsonObject Comment { get; set; } = new JsonObject();
        internal bool ReferencesComplete { get; set; } = true;
        internal bool HasSafetyUse { get; set; }
        internal bool SymbolScanComplete { get; set; } = true;
        internal bool GraphSymbolScanComplete { get; set; } = true;
        internal List<PlcInputAuditReference> References { get; } = new List<PlcInputAuditReference>();
    }

    internal sealed class PlcInputUsageAudit
    {
        internal PlcInputUsageAudit(string[] addressRanges, bool commentEnabled, string[]? keywords,
            bool blockEnabled, string blockType, int blockNumber, string language,
            bool callPinEnabled = false, string? callBlockType = null, int? callBlockNumber = null,
            string? callBlockName = null, bool graphEnabled = false)
        {
            if (addressRanges == null || addressRanges.Length == 0)
                throw new ArgumentException("addressRanges requires at least one input address or inclusive range.");
            Ranges = addressRanges.Select(value => PlcInputSpan.TryRange(value, out var range)
                ? range
                : throw new ArgumentException($"Invalid input address range '{value}'. Use an address such as %I0.0 or a range such as %IB0..%IW4."))
                .ToArray();
            AddressRanges = addressRanges;
            CommentEnabled = commentEnabled;
            Keywords = (keywords ?? new[] { "气缸" }).Select(k => k?.Trim() ?? "")
                .Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            BlockEnabled = blockEnabled;
            BlockType = (blockType ?? "").Trim().ToUpperInvariant();
            BlockNumber = blockNumber;
            Language = (language ?? "").Trim().ToUpperInvariant();
            if (blockEnabled && (BlockType != "FB" && BlockType != "FC" && BlockType != "OB"
                || BlockNumber < 1 || Language.Length == 0))
                throw new ArgumentException("The enabled block rule requires FB, FC or OB, a positive block number and a language.");
            CallPinEnabled = callPinEnabled;
            CallBlockType = (callBlockType ?? "").Trim().ToUpperInvariant();
            CallBlockNumber = callBlockNumber;
            CallBlockName = (callBlockName ?? "").Trim();
            GraphEnabled = graphEnabled;
            if (CallPinEnabled && (CallBlockType is not ("FB" or "FC") || CallBlockNumber is not > 0 || CallBlockName.Length == 0))
                throw new ArgumentException("The enabled call pin rule requires FB or FC, a positive block number and a nonempty block name.");
        }

        private PlcInputSpan[] Ranges { get; }
        private string[] AddressRanges { get; }
        private bool CommentEnabled { get; }
        private string[] Keywords { get; }
        private bool BlockEnabled { get; }
        private string BlockType { get; }
        private int BlockNumber { get; }
        private string Language { get; }
        internal bool CallPinEnabled { get; }
        internal bool GraphEnabled { get; }
        private string CallBlockType { get; }
        private int? CallBlockNumber { get; }
        private string CallBlockName { get; }

        internal bool InRange(PlcInputSpan span) => Ranges.Any(span.Overlaps);
        internal bool MayContainStart(long start) => Ranges.Any(range => start <= range.End);

        // The exact V20 ProgrammingLanguage enum names also exist in V21. F_CALL is generated by Safety.
        internal static PlcInputProgramKind ClassifyProgram(string? language, bool safetyUnit) => safetyUnit
            ? PlcInputProgramKind.Safety
            : language switch
            {
                "F_STL" or "F_LAD" or "F_FBD" or "F_DB" or "F_LAD_LIB" or "F_FBD_LIB" or "F_CALL" => PlcInputProgramKind.Safety,
                "STL" or "LAD" or "FBD" or "SCL" or "DB" or "GRAPH" or "CPU_DB" or "CFC" or "SFC"
                    or "FBD_IEC" or "LAD_IEC" or "SDB" or "S7_PDIAG" or "RSE" or "FCP" or "FLD"
                    or "ProDiag" or "ProDiag_OB" or "Motion_DB" or "CEM" => PlcInputProgramKind.Standard,
                _ => PlcInputProgramKind.Unknown,
            };

        internal static bool CanExport(PlcInputProgramKind kind, bool codeBlock) => kind == PlcInputProgramKind.Standard && codeBlock;

        internal static bool IsMetadata(string? type, string? access)
            => type is "Uses" or "TypeInstance" or "InstanceType" or "Assigns" or "MemberGroup" or "GroupMember"
                or "Defines" or "DefinedBy" or "OverlapsWith" or "Scope"
                || access is "Definition" or "Declaration" or "Interface" or "DefaultValue" or "ArrayBoundary" or "StringLength";

        internal static bool IsRead(string? access) => access is "Read" or "ReadAndSymbol";
        internal static bool IsWrite(string? access) => access is "Write" or "WriteAndSymbol" or "RW" or "ReadWriteAndSymbol";

        internal static List<PlcInputAuditReference> DistinctReferences(IEnumerable<PlcInputAuditReference> references)
        {
            var distinct = references.GroupBy(r => (r.BlockPath, r.BlockType, r.BlockNumber, r.Language, r.Access, r.AccessComplete,
                r.Location, r.Detail, r.Address, r.Origin, r.Absolute, r.ConfirmedUsage, r.Symbol,
                r.GraphRegion, r.TransitionNumber, r.GraphSequence)).Select(g => g.First()).ToList();
            // Native records identify a network. XML identifies the individual wire or token inside it.
            var xml = distinct.Where(r => r.Origin == "simaticML" && r.Absolute && r.Network.HasValue)
                .ToLookup(MatchingAbsoluteAccess);
            return distinct.Where(r => r.Origin != "crossReference" || !r.Absolute || !r.Network.HasValue
                || !xml.Contains(MatchingAbsoluteAccess(r))).ToList();
        }

        private static (string?, int?, string?, string?) MatchingAbsoluteAccess(PlcInputAuditReference r)
            => (r.BlockPath, r.Network, r.Address, IsRead(r.Access) ? "Read" : r.Access == "WriteAndSymbol" ? "Write"
                : r.Access == "ReadWriteAndSymbol" ? "RW" : r.Access);

        internal JsonObject Summarize(IEnumerable<PlcInputAuditPoint> points, IEnumerable<string> warnings,
            bool inventoryComplete, bool absoluteScanComplete, bool callPinScanComplete = true, bool graphScanComplete = true)
        {
            var rows = new JsonArray();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["PASS"] = 0, ["FAIL"] = 0, ["INCOMPLETE"] = 0, ["NOT_EVALUATED"] = 0,
                ["EXCLUDED"] = 0, ["UNUSED"] = 0, ["SAFETY_ONLY"] = 0,
            };
            int used = 0, partial = 0, tags = 0, absoluteOnly = 0, callerFailures = 0, pinFailures = 0, graphFailures = 0;
            var rulesEnabled = BlockEnabled || CallPinEnabled || GraphEnabled;
            bool anyIncomplete = false;
            foreach (var point in points.Where(p => InRange(p.Span)).OrderBy(p => p.Span.Start).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (point.Table == null) absoluteOnly++;
                else tags++;
                var references = DistinctReferences(point.References);
                var hasUse = references.Any(r => r.ConfirmedUsage);
                var reasons = new List<string>();
                var violations = new JsonArray();
                var pinConnections = new JsonArray();
                var graphLocations = new JsonArray();
                var commentError = point.Comment.ContainsKey("_error");
                var excluded = CommentEnabled && point.Comment.Any(c => c.Key != "_error"
                    && c.Value is JsonValue value && value.TryGetValue<string>(out var comment)
                    && Keywords.Any(k => comment.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
                var identityComplete = inventoryComplete && point.ReferencesComplete && point.SymbolScanComplete
                    && references.All(r => r.ConfirmedUsage);
                var usageComplete = identityComplete && absoluteScanComplete;
                var callerComplete = usageComplete;
                var pinComplete = usageComplete && callPinScanComplete;
                var graphComplete = inventoryComplete && graphScanComplete && point.GraphSymbolScanComplete
                    && references.All(r => r.GraphRegion == null || r.ConfirmedUsage);
                bool callerFail = false, pinFail = false, graphFail = false, targetInput = false;

                void Violation(string rule, string code, string message, PlcInputAuditReference? reference = null)
                {
                    reasons.Add(message);
                    violations.Add(new JsonObject
                    {
                        ["rule"] = rule, ["code"] = code, ["message"] = message,
                        ["blockPath"] = reference?.BlockPath, ["location"] = reference?.Location,
                    });
                }

                if (!excluded)
                {
                    if (CommentEnabled && commentError) reasons.Add("Multilingual comments could not be read completely.");
                    if (!inventoryComplete) reasons.Add("The standard-program inventory is incomplete.");
                    if (!point.ReferencesComplete) reasons.Add("Native references could not be read completely.");
                    if (!point.SymbolScanComplete) reasons.Add("An XML symbol could not be resolved to a unique input identity.");
                    if (!absoluteScanComplete) reasons.Add("The absolute-address scan is incomplete.");
                    if (CallPinEnabled && !callPinScanComplete) reasons.Add("The call-pin scan is incomplete.");
                    if (GraphEnabled && !graphScanComplete) reasons.Add("The GRAPH scan is incomplete.");
                }
                foreach (var reference in references)
                {
                    var site = (reference.BlockPath ?? "(unresolved user)") + " " + reference.Location;
                    if (!excluded && !reference.ConfirmedUsage)
                        reasons.Add($"{site}: the usage could not be confirmed.");
                    if (BlockEnabled && !excluded && reference.ConfirmedUsage)
                    {
                        if (reference.BlockType != null && reference.BlockType != BlockType
                            || reference.BlockNumber.HasValue && reference.BlockNumber != BlockNumber
                            || reference.Language != null && !string.Equals(reference.Language, Language, StringComparison.OrdinalIgnoreCase))
                        {
                            callerFail = true;
                            Violation("caller", "WRONG_CALLER", $"{site}: expected {BlockType}{BlockNumber} {Language}, found {reference.BlockType}{reference.BlockNumber} {reference.Language}.", reference);
                        }
                        if (reference.BlockType == null || !reference.BlockNumber.HasValue || reference.Language == null)
                        {
                            callerComplete = false;
                            reasons.Add($"{site}: the caller block properties could not be confirmed.");
                        }
                        if (IsWrite(reference.Access))
                        {
                            callerFail = true;
                            Violation("caller", "NOT_READ_ONLY", $"{site}: {reference.Access} is not read-only.", reference);
                        }
                        if (!reference.AccessComplete || (!IsRead(reference.Access) && !IsWrite(reference.Access)))
                        {
                            callerComplete = false;
                            reasons.Add($"{site}: unconfirmed access ({reference.Access ?? "undefined"}).");
                        }
                    }
                    foreach (var pin in reference.CallPins)
                    {
                        var connection = pin.ToJson();
                        connection["callerBlockName"] = reference.BlockName;
                        connection["callerBlockType"] = reference.BlockType;
                        connection["callerBlockNumber"] = reference.BlockNumber;
                        connection["callerBlockPath"] = reference.BlockPath;
                        connection["network"] = reference.Network;
                        connection["location"] = reference.Location;
                        connection["confirmed"] = pin.Confirmed && reference.ConfirmedUsage;
                        pinConnections.Add(connection);
                        if (!CallPinEnabled || excluded || !reference.ConfirmedUsage) continue;
                        var wrongDirection = pin.Direction is "Output" or "Return";
                        var wrongTarget = pin.BlockNumber.HasValue && (pin.BlockType != CallBlockType
                            || pin.BlockNumber != CallBlockNumber || !string.Equals(pin.BlockName, CallBlockName, StringComparison.OrdinalIgnoreCase));
                        if (wrongDirection || wrongTarget)
                        {
                            pinFail = true;
                            Violation("callPin", wrongDirection ? "OUTPUT_OR_RETURN" : "WRONG_CALLED_BLOCK",
                                $"{site}: {pin.BlockType} {pin.BlockName} ({pin.BlockNumber}) pin {pin.Pin} ({pin.Direction}).", reference);
                        }
                        if (!pin.Confirmed)
                        {
                            pinComplete = false;
                            reasons.Add($"{site}: call target or pin direction could not be confirmed.");
                        }
                        else if (!wrongDirection && !wrongTarget) targetInput = true;
                    }

                    if (reference.GraphRegion == null) continue;
                    var graphLocation = reference.ToJson();
                    graphLocation["inputAddress"] = point.Address;
                    graphLocation["inputName"] = point.Name;
                    graphLocations.Add(graphLocation);
                    if (!GraphEnabled || excluded || !reference.ConfirmedUsage) continue;
                    graphFail = true;
                    Violation("graphDirectUse", "DIRECT_GRAPH_INPUT", $"{site}: direct input use in {reference.GraphRegion}.", reference);
                }
                if (CallPinEnabled && !excluded && hasUse && pinComplete && !targetInput)
                {
                    pinFail = true;
                    Violation("callPin", "MISSING_TARGET_INPUT", $"No direct Input or InOut connection was found to {CallBlockType}{CallBlockNumber} {CallBlockName}.");
                }

                var complete = excluded || (usageComplete && (!CommentEnabled || !commentError)
                    && (!BlockEnabled || callerComplete) && (!CallPinEnabled || pinComplete) && (!GraphEnabled || graphComplete));
                anyIncomplete |= !complete;
                var callerStatus = RuleStatus(BlockEnabled, callerFail, callerComplete);
                var pinStatus = RuleStatus(CallPinEnabled, pinFail, pinComplete);
                var graphStatus = RuleStatus(GraphEnabled, graphFail, graphComplete);
                var status = excluded ? "EXCLUDED" : callerFail || pinFail || graphFail ? "FAIL" : !complete ? "INCOMPLETE"
                    : !hasUse ? point.HasSafetyUse ? "SAFETY_ONLY" : "UNUSED" : rulesEnabled ? "PASS" : "NOT_EVALUATED";
                counts[status]++;
                if (!excluded && hasUse) used++;
                if (status == "FAIL" && !complete) partial++;
                if (callerFail) callerFailures++;
                if (pinFail) pinFailures++;
                if (graphFail) graphFailures++;
                rows.Add(new JsonObject
                {
                    ["name"] = point.Name, ["address"] = point.Address, ["coveredAddress"] = point.Span.Canonical,
                    ["table"] = point.Table, ["softwareUnit"] = point.SoftwareUnit, ["comment"] = point.Comment.DeepClone(),
                    ["references"] = new JsonArray(references.Select(r => (JsonNode)r.ToJson()).ToArray()),
                    ["callPinConnections"] = pinConnections, ["graphLocations"] = graphLocations,
                    ["status"] = status, ["ruleStatus"] = callerStatus, ["callerRuleStatus"] = callerStatus,
                    ["callPinRuleStatus"] = pinStatus, ["graphDirectUseStatus"] = graphStatus,
                    ["complete"] = complete, ["hasSafetyUse"] = point.HasSafetyUse,
                    ["commentStatus"] = !CommentEnabled ? "NOT_EVALUATED" : excluded ? "EXCLUDED" : commentError ? "INCOMPLETE" : "PASS",
                    ["reasons"] = Strings(reasons.Distinct()), ["violations"] = violations,
                });

                string RuleStatus(bool enabled, bool fail, bool ruleComplete) => !enabled || excluded ? "NOT_EVALUATED"
                    : fail ? "FAIL" : !ruleComplete ? "INCOMPLETE" : !hasUse ? "UNUSED" : "PASS";
            }

            var checkedCount = counts["PASS"] + counts["FAIL"] + counts["INCOMPLETE"] + counts["NOT_EVALUATED"];
            var allComplete = inventoryComplete && absoluteScanComplete && (!CallPinEnabled || callPinScanComplete)
                && (!GraphEnabled || graphScanComplete) && !anyIncomplete;
            return new JsonObject
            {
                ["addressRanges"] = Strings(AddressRanges),
                ["configuration"] = new JsonObject
                {
                    ["commentFilterEnabled"] = CommentEnabled, ["commentKeywords"] = Strings(Keywords),
                    ["fbRuleEnabled"] = BlockEnabled, ["requiredBlockType"] = BlockType,
                    ["requiredBlockNumber"] = BlockNumber, ["requiredLanguage"] = Language,
                    ["callPinRuleEnabled"] = CallPinEnabled, ["requiredCallBlockType"] = CallBlockType,
                    ["requiredCallBlockNumber"] = CallBlockNumber, ["requiredCallBlockName"] = CallBlockName,
                    ["graphTransitionRuleEnabled"] = GraphEnabled,
                },
                ["rangeTagCount"] = tags, ["absoluteOnlyCount"] = absoluteOnly, ["excludedCount"] = counts["EXCLUDED"],
                ["usedCount"] = used, ["unusedCount"] = counts["UNUSED"], ["safetyOnlyExcludedCount"] = counts["SAFETY_ONLY"],
                ["checkedInputCount"] = checkedCount, ["passCount"] = counts["PASS"], ["failCount"] = counts["FAIL"],
                ["unconfirmedCount"] = counts["INCOMPLETE"], ["partiallyUnconfirmedCount"] = partial,
                ["legacyUnconfirmedCount"] = counts["INCOMPLETE"] + partial, ["notEvaluatedCount"] = counts["NOT_EVALUATED"],
                ["ruleViolationCounts"] = new JsonObject
                {
                    ["caller"] = callerFailures, ["callPin"] = pinFailures, ["graphDirectUse"] = graphFailures,
                },
                ["points"] = rows, ["warnings"] = Strings(warnings), ["complete"] = allComplete,
                ["overallStatus"] = counts["FAIL"] > 0 ? "FAIL" : !allComplete ? "INCOMPLETE"
                    : !rulesEnabled ? "NOT_EVALUATED" : checkedCount == 0 ? "UNUSED" : "PASS",
            };
        }

        internal static JsonArray Strings(IEnumerable<string> values)
            => new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }
}
