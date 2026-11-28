using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
        Protected,
        ProDiag,
        System,
        TechnologyObject,
    }

    internal enum PlcInputPinSource
    {
        Direct,
        Contact,
        NegatedContact,
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
        internal PlcInputPinSource Source { get; set; }
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
        internal bool HasOtherWrite { get; set; }
        internal bool NetworkScanComplete { get; set; }
        internal bool CallPinTraceComplete { get; set; } = true;
        internal string? Location { get; set; }
        internal string? Detail { get; set; }
        internal string? Address { get; set; }
        internal string Origin { get; set; } = "crossReference";
        internal bool Absolute { get; set; }
        internal bool ConfirmedUsage { get; set; } = true;
        internal int? Network { get; set; }
        internal string? BlockName { get; set; }
        internal string? Symbol { get; set; }
        internal string[]? SymbolComponents { get; set; }
        internal string? GraphRegion { get; set; }
        internal int? GraphOperationNumber { get; set; }
        internal string? TransitionName { get; set; }
        internal int? TransitionNumber { get; set; }
        internal int? GraphSequence { get; set; }
        internal List<PlcInputCallPin> CallPins { get; } = new List<PlcInputCallPin>();
        internal IReadOnlyList<PlcInputCallPin> SimaticMlPins => Origin == "simaticML" ? CallPins : Array.Empty<PlcInputCallPin>();
        internal bool IsDirectInOutWrite => ConfirmedUsage && AccessComplete && !HasOtherWrite
            && (Access is "RW" or "ReadWriteAndSymbol")
            && SimaticMlPins.Any(p => p.Source == PlcInputPinSource.Direct && p.Direction == "InOut")
            && SimaticMlPins.All(p => p.Direction is "Input" or "InOut");

        internal JsonObject ToJson(bool inOutWriteAllowed = false, PlcInputAuditReference? caller = null) => new JsonObject
        {
            ["blockPath"] = caller?.BlockPath ?? BlockPath, ["blockType"] = caller?.BlockType ?? BlockType,
            ["blockNumber"] = caller?.BlockNumber ?? BlockNumber, ["blockName"] = caller?.BlockName ?? BlockName,
            ["callerOrigin"] = caller?.Origin ?? Origin,
            ["language"] = Language, ["access"] = Access, ["location"] = Location, ["detail"] = Detail,
            ["accessComplete"] = AccessComplete && (PlcInputUsageAudit.IsRead(Access) || PlcInputUsageAudit.IsWrite(Access)),
            ["inOutWriteAllowed"] = inOutWriteAllowed,
            ["address"] = Address, ["origin"] = Origin, ["absolute"] = Absolute, ["confirmedUsage"] = ConfirmedUsage,
            ["network"] = Network, ["symbol"] = Symbol, ["graphRegion"] = GraphRegion,
            ["transitionName"] = TransitionName, ["transitionNumber"] = TransitionNumber,
            ["graphSequence"] = GraphSequence, ["graphOperationNumber"] = GraphOperationNumber,
            ["callPins"] = new JsonArray(SimaticMlPins.Select(p => (JsonNode)p.ToJson()).ToArray()),
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
        internal List<string> ReferenceIssues { get; } = new List<string>();
    }

    public sealed class PlcInputAllowedUsage
    {
        [JsonPropertyName("fbReadOnly")] public PlcInputAuditCallerRule FbReadOnly { get; set; } = new PlcInputAuditCallerRule();
        [JsonPropertyName("callPinTarget")] public PlcInputAuditPinRule CallPinTarget { get; set; } = new PlcInputAuditPinRule();
    }

    public sealed class PlcInputAuditCallerRule
    {
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
        [JsonPropertyName("targetBlockType")] public string? TargetBlockType { get; set; }
        [JsonPropertyName("targetBlockNumber")] public int TargetBlockNumber { get; set; }
        [JsonPropertyName("targetLanguage")] public string? TargetLanguage { get; set; }
    }

    public sealed class PlcInputAuditPinRule
    {
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
        [JsonPropertyName("targetBlockType")] public string? TargetBlockType { get; set; }
        [JsonPropertyName("targetBlockNumber")] public int? TargetBlockNumber { get; set; }
        [JsonPropertyName("targetBlockName")] public string? TargetBlockName { get; set; }
    }

    internal sealed class PlcInputUsageAudit
    {
        private static readonly Regex NativeGraphOperation = new Regex(
            @"(?:^| ▶ )(?<side>前|后)(?:永久|固定)指令\s+(?<number>[0-9]+)(?=\s|$)", RegexOptions.CultureInvariant);

        internal PlcInputUsageAudit(string[] addressRanges, bool commentEnabled, string[]? keywords,
            bool blockEnabled, string blockType, int blockNumber, string language,
            bool callPinEnabled = false, string? callBlockType = null, int? callBlockNumber = null,
            string? callBlockName = null, bool graphEnabled = false,
            int[]? skipFbNumbers = null, string[]? skipFbNames = null, PlcInputAllowedUsage[]? allowedUsages = null)
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
            if (allowedUsages is { Length: 0 }) throw new ArgumentException("allowedUsages requires at least one caller/callee pair.");
            AllowedUsages = allowedUsages?.Select(NormalizeAllowedUsage).ToArray();
            BlockEnabled = blockEnabled || AllowedUsages != null;
            BlockType = (blockType ?? "").Trim().ToUpperInvariant();
            BlockNumber = blockNumber;
            Language = (language ?? "").Trim().ToUpperInvariant();
            if (AllowedUsages == null && blockEnabled && (BlockType != "FB" && BlockType != "FC" && BlockType != "OB"
                || BlockNumber < 1 || Language.Length == 0))
                throw new ArgumentException("The enabled block rule requires FB, FC or OB, a positive block number and a language.");
            CallPinEnabled = callPinEnabled || AllowedUsages != null;
            CallBlockType = (callBlockType ?? "").Trim().ToUpperInvariant();
            CallBlockNumber = callBlockNumber;
            CallBlockName = (callBlockName ?? "").Trim();
            GraphEnabled = graphEnabled;
            if (skipFbNumbers?.Any(n => n < 1) == true || skipFbNames?.Any(n => string.IsNullOrWhiteSpace(n)) == true)
                throw new ArgumentException("Skip FB selectors must be positive block numbers or nonempty names.");
            SkipFbNumbers = new HashSet<int>(skipFbNumbers ?? Array.Empty<int>());
            SkipFbNames = new HashSet<string>((skipFbNames ?? Array.Empty<string>()).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            if (AllowedUsages == null && CallPinEnabled && (CallBlockType is not ("FB" or "FC") || CallBlockNumber is not > 0 || CallBlockName.Length == 0))
                throw new ArgumentException("The enabled call pin rule requires FB or FC, a positive block number and a nonempty block name.");
        }

        private static PlcInputAllowedUsage NormalizeAllowedUsage(PlcInputAllowedUsage? usage, int index)
        {
            var caller = usage?.FbReadOnly;
            var callee = usage?.CallPinTarget;
            var callerType = caller?.TargetBlockType?.Trim().ToUpperInvariant();
            var language = caller?.TargetLanguage?.Trim().ToUpperInvariant();
            var calledType = callee?.TargetBlockType?.Trim().ToUpperInvariant();
            var calledName = callee?.TargetBlockName?.Trim();
            if (caller?.Enabled == null || callerType is not ("FB" or "FC" or "OB") || caller.TargetBlockNumber < 1
                || string.IsNullOrEmpty(language) || callee?.Enabled == null || calledType is not ("FB" or "FC")
                || callee.TargetBlockNumber is <= 0 || (callee.TargetBlockNumber == null && string.IsNullOrEmpty(calledName)))
                throw new ArgumentException($"allowedUsages[{index}] requires fbReadOnly with caller type, number and language, and callPinTarget with FB/FC type and a positive number or name. Both objects require enabled.");
            return new PlcInputAllowedUsage
            {
                FbReadOnly = new PlcInputAuditCallerRule { Enabled = caller.Enabled, TargetBlockType = callerType,
                    TargetBlockNumber = caller.TargetBlockNumber, TargetLanguage = language },
                CallPinTarget = new PlcInputAuditPinRule { Enabled = callee.Enabled, TargetBlockType = calledType,
                    TargetBlockNumber = callee.TargetBlockNumber, TargetBlockName = string.IsNullOrEmpty(calledName) ? null : calledName },
            };
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
        private PlcInputAllowedUsage[]? AllowedUsages { get; }
        private HashSet<int> SkipFbNumbers { get; }
        private HashSet<string> SkipFbNames { get; }
        internal bool SkipBlock(string type, int number, string name)
            => type == "FB" && (SkipFbNumbers.Contains(number) || SkipFbNames.Contains(name));

        internal bool InRange(PlcInputSpan span) => Ranges.Any(span.Overlaps);
        internal bool MayContainStart(long start) => Ranges.Any(range => start <= range.End);

        internal static PlcInputProgramKind ClassifyProgram(string? language, bool safetyUnit, string? blockType = null,
            bool systemBlock = false, bool? knowHowProtected = false) => systemBlock ? PlcInputProgramKind.System
            : safetyUnit ? PlcInputProgramKind.Safety
            : (language, blockType) switch
            {
                ("F_STL" or "F_LAD" or "F_FBD" or "F_DB" or "F_LAD_LIB" or "F_FBD_LIB" or "F_CALL", _) => PlcInputProgramKind.Safety,
                ("ProDiag", "FB") or ("ProDiag_OB", "OB") => PlcInputProgramKind.ProDiag,
                _ when knowHowProtected == true => PlcInputProgramKind.Protected,
                _ when knowHowProtected == null => PlcInputProgramKind.Unknown,
                ("STL" or "LAD" or "FBD" or "SCL" or "DB" or "GRAPH" or "CPU_DB" or "CFC" or "SFC"
                    or "FBD_IEC" or "LAD_IEC" or "SDB" or "S7_PDIAG" or "RSE" or "FCP" or "FLD"
                    or "Motion_DB" or "CEM", _) => PlcInputProgramKind.Standard,
                _ => PlcInputProgramKind.Unknown,
            };

        internal static bool CanExport(PlcInputProgramKind kind, bool codeBlock) => kind == PlcInputProgramKind.Standard && codeBlock;

        internal static void AddNativeProgramUse(PlcInputAuditPoint point, PlcInputAuditReference reference,
            PlcInputProgramKind kind, bool skipped)
        {
            switch (kind)
            {
                case PlcInputProgramKind.Unknown:
                    point.ReferencesComplete = false;
                    point.ReferenceIssues.Add($"Could not confirm the block identity at {reference.BlockPath} {reference.Location}.");
                    break;
                case PlcInputProgramKind.Safety:
                    point.HasSafetyUse |= reference.ConfirmedUsage;
                    break;
                case PlcInputProgramKind.Standard when !skipped:
                    point.References.Add(reference);
                    break;
            }
        }

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
                r.GraphRegion, r.TransitionNumber, r.GraphSequence, r.HasOtherWrite, r.IsDirectInOutWrite,
                r.CallPinTraceComplete)).Select(g => g.First()).ToList();
            // Native records identify a network. XML identifies the individual wire or token inside it.
            var xml = distinct.Where(r => r.Origin == "simaticML" && r.Absolute && r.Network.HasValue && r.ConfirmedUsage)
                .ToLookup(MatchingAbsoluteAccess);
            return distinct.Where(r => r.Origin != "crossReference" || !r.Absolute || !r.Network.HasValue || !r.ConfirmedUsage
                || !xml.Contains(MatchingAbsoluteAccess(r))
                || xml[MatchingAbsoluteAccess(r)].Any(match => match.IsDirectInOutWrite)).ToList();
        }

        private static (string?, int?, string?, string?) MatchingAbsoluteAccess(PlcInputAuditReference r)
            => (r.BlockPath, r.Network, r.Address, AccessKind(r.Access));

        private static string? AccessKind(string? access) => access switch
        {
            "ReadAndSymbol" => "Read", "WriteAndSymbol" => "Write", "ReadWriteAndSymbol" => "RW", _ => access,
        };

        private static bool MatchesSymbol(PlcInputAuditReference native, PlcInputAuditReference operand, string? pointName)
            => operand.SymbolComponents is { } components && (string.IsNullOrEmpty(native.Symbol)
                ? components.Length == 1 && string.Equals(pointName, components[0], StringComparison.OrdinalIgnoreCase)
                : NativeSymbolMatches(native.Symbol, components));

        private static (Dictionary<PlcInputAuditReference, PlcInputAuditReference> Callers,
            HashSet<PlcInputAuditReference> CoveredNative) FindMatchingReferences(
            List<PlcInputAuditReference> references, string? pointName, bool includeAbsolute)
        {
            // Keep both sources for rule evaluation. Suppress only a network-level summary
            // that is represented by a confirmed, matching XML site in the result.
            var comparable = references.Where(r => (!r.Absolute || includeAbsolute) && r.BlockPath != null && r.ConfirmedUsage
                && r.AccessComplete && (IsRead(r.Access) || IsWrite(r.Access)))
                .Select(r => (Reference: r, Site: Site(r)))
                .Where(r => r.Site.Network.HasValue || r.Site.Operation.HasValue);
            var operands = comparable.Where(r => r.Reference.Origin == "simaticML")
                .ToLookup(r => (r.Reference.BlockPath, r.Reference.Language, AccessKind(r.Reference.Access), r.Reference.Absolute, r.Site), r => r.Reference);
            var callerByOperand = new Dictionary<PlcInputAuditReference, PlcInputAuditReference>();
            var coveredNative = new HashSet<PlcInputAuditReference>();
            foreach (var (native, site) in comparable.Where(r => r.Reference.Origin == "crossReference"))
            {
                var matches = operands[(native.BlockPath, native.Language, AccessKind(native.Access), native.Absolute, site)]
                    .Where(r => native.Absolute ? native.Address != null && r.Address == native.Address
                        : MatchesSymbol(native, r, pointName)).ToList();
                // A GRAPH operation number is scoped to its region within one compile unit.
                if (site.Operation.HasValue && (matches.Select(r => r.Network).Distinct().Count() != 1
                    || matches.Any(r => !r.NetworkScanComplete || !r.Network.HasValue
                        || native.Network.HasValue && r.Network != native.Network))) continue;
                foreach (var operand in matches)
                {
                    if (!callerByOperand.ContainsKey(operand)) callerByOperand.Add(operand, native);
                    coveredNative.Add(native);
                }
            }

            return (callerByOperand, coveredNative);

            static (int? Network, string? Region, int? Operation) Site(PlcInputAuditReference reference)
            {
                if (reference.Language != "GRAPH") return (reference.Network, null, null);
                if (reference.Origin == "simaticML") return (null, reference.GraphRegion, reference.GraphOperationNumber);
                var match = NativeGraphOperation.Match(reference.Location ?? "");
                return int.TryParse(match.Groups["number"].Value, out var number) && number > 0
                    ? (null, match.Groups["side"].Value == "前" ? "PermanentPreInstructions" : "PermanentPostInstructions", number)
                    : default;
            }
        }

        internal static bool NativeSymbolMatches(string? native, string[] components)
        {
            if (string.IsNullOrWhiteSpace(native)) return false;
            var pattern = string.Join(@"\.", components.Select(component =>
            {
                var quoted = Regex.Escape("\"" + component.Replace("\"", "\"\"") + "\"");
                return component.IndexOfAny(new[] { '.', '"', '[' }) < 0
                    ? "(?:" + quoted + "|" + Regex.Escape(component) + ")" : quoted;
            }));
            return Regex.IsMatch(native!, "\\A" + pattern + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static (bool Allowed, bool Unconfirmed) InOutWriteAssessment(PlcInputAuditReference reference,
            ILookup<(string?, bool), PlcInputAuditReference> xmlByBlock, string? pointName)
        {
            if (reference.IsDirectInOutWrite) return (true, false);
            // Native references can summarize a network rather than identify the individual wire.
            if (reference.Origin != "crossReference" || !IsWrite(reference.Access) || reference.BlockPath == null) return default;
            var networkKnown = reference.Network.HasValue && reference.Language != "GRAPH";
            var operands = xmlByBlock[(reference.BlockPath, reference.Absolute)]
                .Where(r => !networkKnown || reference.Network == r.Network)
                .Where(r => reference.Absolute ? reference.Address == r.Address : MatchesSymbol(reference, r, pointName))
                .ToList();
            if (!operands.Any(r => r.IsDirectInOutWrite)
                || operands.Any(r => r.ConfirmedUsage && IsWrite(r.Access) && !r.IsDirectInOutWrite)) return default;
            var complete = networkKnown && operands.All(r => r.NetworkScanComplete && r.ConfirmedUsage && r.AccessComplete
                && (IsRead(r.Access) || r.IsDirectInOutWrite));
            return (complete, !complete);
        }

        internal JsonObject Summarize(IEnumerable<PlcInputAuditPoint> points, IEnumerable<string> warnings,
            bool inventoryComplete, bool absoluteScanComplete, bool callPinScanComplete = true, bool graphScanComplete = true)
        {
            var rows = new JsonArray();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["PASS"] = 0, ["FAIL"] = 0, ["INCOMPLETE"] = 0, ["NOT_EVALUATED"] = 0,
                ["EXCLUDED"] = 0, ["UNUSED"] = 0,
            };
            int used = 0, partial = 0, tags = 0, absoluteOnly = 0, callerFailures = 0, pinFailures = 0, graphFailures = 0;
            int safetyOnlyExcluded = 0, commentFailures = 0;
            var rulesEnabled = BlockEnabled || CallPinEnabled || GraphEnabled;
            bool anyIncomplete = false;
            foreach (var point in points.Where(p => InRange(p.Span)).OrderBy(p => p.Span.Start).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (point.Table == null) absoluteOnly++;
                else tags++;
                var references = DistinctReferences(point.References);
                var hasUse = references.Any(r => r.ConfirmedUsage);
                var identityComplete = inventoryComplete && point.ReferencesComplete && point.SymbolScanComplete
                    && references.All(r => r.ConfirmedUsage);
                var usageComplete = identityComplete && absoluteScanComplete && graphScanComplete && point.GraphSymbolScanComplete;
                if (!hasUse && usageComplete && (!CallPinEnabled || callPinScanComplete))
                {
                    counts["UNUSED"]++;
                    if (point.HasSafetyUse) safetyOnlyExcluded++;
                    continue;
                }
                var commentError = point.Comment.ContainsKey("_error");
                var comments = point.Comment.Where(c => c.Key != "_error" && c.Value is JsonValue value
                    && value.TryGetValue<string>(out _)).Select(c => c.Value!.GetValue<string>()).ToArray();
                var excluded = CommentEnabled && comments.Any(comment =>
                    Keywords.Any(k => comment.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
                var (callers, coveredNative) = FindMatchingReferences(references, point.Name, AllowedUsages != null);
                var xmlByBlock = references.Where(r => r.Origin == "simaticML")
                    .ToLookup(r => (r.BlockPath, r.Absolute));
                var allowedInOut = new HashSet<PlcInputAuditReference>();
                var assessments = new Dictionary<PlcInputAuditReference, (JsonObject Use, List<JsonObject> Pins)>();
                var reasons = new List<string>();
                var violations = new JsonArray();
                var pinConnections = new JsonArray();
                var graphLocations = new JsonArray();
                var checkComment = !excluded && hasUse && point.Name != null;
                var commentFail = checkComment && !commentError && comments.All(string.IsNullOrWhiteSpace);
                var commentStatus = excluded ? "EXCLUDED" : !checkComment ? "NOT_EVALUATED"
                    : commentError ? "INCOMPLETE" : commentFail ? "FAIL" : "PASS";
                JsonObject? commentViolation = null;
                var callerComplete = usageComplete;
                var pinComplete = usageComplete && callPinScanComplete;
                var graphComplete = inventoryComplete && graphScanComplete && point.GraphSymbolScanComplete
                    && references.All(r => r.GraphRegion == null || r.ConfirmedUsage);
                bool callerFail = false, pinFail = false, graphFail = false, hasTargetConnection = false;

                JsonObject Violation(string rule, string code, string message, PlcInputAuditReference? reference = null)
                {
                    var violation = new JsonObject
                    {
                        ["rule"] = rule, ["code"] = code, ["message"] = message,
                        ["blockPath"] = reference?.BlockPath, ["location"] = reference?.Location,
                    };
                    violations.Add(violation);
                    return violation;
                }

                if (!excluded)
                {
                    if (commentFail)
                    {
                        commentViolation = new JsonObject
                        {
                            ["rule"] = "comment", ["code"] = "MISSING_INPUT_COMMENT", ["message"] = "I 点缺少注释。",
                        };
                        violations.Add(commentViolation);
                    }
                    if (checkComment && commentError) reasons.Add("Multilingual comments could not be read completely.");
                    if (!inventoryComplete) reasons.Add("The standard-program inventory is incomplete.");
                    if (!point.ReferencesComplete)
                    {
                        reasons.Add("Native references could not be read completely.");
                        reasons.AddRange(point.ReferenceIssues);
                    }
                    if (!point.SymbolScanComplete) reasons.Add("An XML symbol could not be resolved to a unique input identity.");
                    if (!absoluteScanComplete) reasons.Add("The absolute-address scan is incomplete.");
                    if (CallPinEnabled && !callPinScanComplete) reasons.Add("The call-pin scan is incomplete.");
                    if (!graphScanComplete) reasons.Add("The GRAPH scan is incomplete.");
                    if (!point.GraphSymbolScanComplete) reasons.Add("A GRAPH symbol could not be resolved to a unique input identity.");
                }
                foreach (var reference in references)
                {
                    var site = (reference.BlockPath ?? "(unresolved user)") + " " + reference.Location;
                    var useViolations = new List<JsonObject>();
                    var pinAssessments = new List<JsonObject>();
                    var callerUseComplete = reference.ConfirmedUsage;
                    bool callerUseFail = false, graphUseFail = false;
                    var callerIdentityComplete = reference.BlockType != null && reference.BlockNumber.HasValue && reference.Language != null;
                    int CallerDifferences(PlcInputAllowedUsage rule)
                        => (reference.BlockType != null && reference.BlockType != rule.FbReadOnly.TargetBlockType ? 1 : 0)
                            + (reference.BlockNumber.HasValue && reference.BlockNumber != rule.FbReadOnly.TargetBlockNumber ? 1 : 0)
                            + (reference.Language != null && !string.Equals(reference.Language, rule.FbReadOnly.TargetLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                    var callerOptions = AllowedUsages?.Where(rule => CallerDifferences(rule) == 0).ToList();
                    var (inOutAllowed, inOutUnconfirmed) = InOutWriteAssessment(reference, xmlByBlock, point.Name);
                    if (inOutAllowed) allowedInOut.Add(reference);
                    if (!excluded && !reference.ConfirmedUsage)
                        reasons.Add($"{site}: the usage could not be confirmed.");
                    if (CallPinEnabled && !excluded && !reference.CallPinTraceComplete)
                    {
                        pinComplete = false;
                        reasons.Add($"{site}: the LAD contact output could not be traced unambiguously.");
                    }
                    if (BlockEnabled && !excluded && reference.ConfirmedUsage)
                    {
                        void CheckCaller(string field, string expected, string? actual)
                        {
                            if (actual == null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return;
                            callerFail = callerUseFail = true;
                            var violation = Violation("caller", "WRONG_CALLER", $"{site}: expected caller {field} {expected}, found {actual}.", reference);
                            violation["field"] = field;
                            violation["expected"] = expected;
                            violation["actual"] = actual;
                            useViolations.Add(violation);
                        }
                        if (callerOptions == null || callerOptions.Count == 0)
                        {
                            var closest = AllowedUsages?.OrderBy(CallerDifferences).First().FbReadOnly;
                            CheckCaller("blockType", closest?.TargetBlockType ?? BlockType, reference.BlockType);
                            CheckCaller("blockNumber", (closest?.TargetBlockNumber ?? BlockNumber).ToString(CultureInfo.InvariantCulture),
                                reference.BlockNumber?.ToString(CultureInfo.InvariantCulture));
                            CheckCaller("language", closest?.TargetLanguage ?? Language, reference.Language);
                        }
                        if (!callerIdentityComplete)
                        {
                            callerComplete = callerUseComplete = false;
                            reasons.Add($"{site}: the caller block properties could not be confirmed.");
                        }
                        if (AllowedUsages == null && inOutUnconfirmed)
                        {
                            callerComplete = callerUseComplete = false;
                            reasons.Add($"{site}: the native write could not be attributed exclusively to the verified InOut connection.");
                        }
                        else if (AllowedUsages == null && IsWrite(reference.Access) && !inOutAllowed)
                        {
                            callerFail = callerUseFail = true;
                            useViolations.Add(Violation("caller", "NOT_READ_ONLY", $"{site}: {reference.Access} is not read-only.", reference));
                        }
                        if (AllowedUsages == null && (!reference.AccessComplete || (!IsRead(reference.Access) && !IsWrite(reference.Access))))
                        {
                            callerComplete = callerUseComplete = false;
                            reasons.Add($"{site}: unconfirmed access ({reference.Access ?? "undefined"}).");
                        }
                    }
                    if (reference.GraphRegion != null)
                    {
                        var graphLocation = reference.ToJson();
                        graphLocation["inputAddress"] = point.Address;
                        graphLocation["inputName"] = point.Name;
                        graphLocations.Add(graphLocation);
                        if (GraphEnabled && !excluded && reference.ConfirmedUsage)
                        {
                            graphFail = graphUseFail = true;
                            var violation = Violation("graphDirectUse", "DIRECT_GRAPH_INPUT", $"{site}: direct input use in {reference.GraphRegion}.", reference);
                            violation["graphRegion"] = reference.GraphRegion;
                            useViolations.Add(violation);
                        }
                    }
                    var graphUseComplete = reference.ConfirmedUsage && (reference.GraphRegion != null
                        || reference.Language != null && (reference.Language != "GRAPH" || reference.Origin == "simaticML"));
                    var useGraphStatus = RuleStatus(GraphEnabled, graphUseFail, graphUseComplete);
                    JsonObject AssessUse(bool callerFailed, bool callerConfirmed, string usePinStatus, bool pinUseComplete,
                        IEnumerable<JsonObject> rowViolations)
                    {
                        var useCallerStatus = RuleStatus(BlockEnabled, callerFailed, callerConfirmed);
                        var useCommentStatus = reference.ConfirmedUsage ? commentStatus : "NOT_EVALUATED";
                        var statuses = new[] { useCallerStatus, usePinStatus, useGraphStatus, useCommentStatus };
                        if (reference.ConfirmedUsage && commentViolation != null)
                            rowViolations = rowViolations.Append(commentViolation);
                        return new JsonObject
                        {
                            ["callerRuleStatus"] = useCallerStatus, ["callPinRuleStatus"] = usePinStatus,
                            ["graphDirectUseStatus"] = useGraphStatus, ["commentStatus"] = useCommentStatus,
                            ["status"] = statuses.Contains("FAIL") ? "FAIL" : statuses.Contains("INCOMPLETE") ? "INCOMPLETE"
                                : statuses.Contains("PASS") ? "PASS" : "NOT_EVALUATED",
                            ["complete"] = !statuses.Contains("INCOMPLETE") && (excluded
                                || (!BlockEnabled || callerConfirmed) && (!CallPinEnabled || pinUseComplete)),
                            ["violations"] = new JsonArray(rowViolations.Select(v => v.DeepClone()).ToArray()),
                        };
                    }

                    foreach (var pin in reference.SimaticMlPins)
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
                        var pinViolations = new List<JsonObject>();
                        bool rowCallerFail = callerUseFail, rowCallerComplete = callerUseComplete, rowPinFail = false;
                        var pinUseComplete = reference.ConfirmedUsage && pin.Confirmed
                            && (AllowedUsages == null || callerIdentityComplete);
                        if (CallPinEnabled && !excluded && reference.ConfirmedUsage)
                        {
                            var outputOrReturn = pin.Direction is "Output" or "Return";
                            var wrongDirection = AllowedUsages == null && outputOrReturn;
                            bool wrongTarget = false, wrongPair = false;
                            if (pin.BlockNumber.HasValue)
                            {
                                if (AllowedUsages == null)
                                    wrongTarget = pin.BlockType != CallBlockType || pin.BlockNumber != CallBlockNumber
                                        || !string.Equals(pin.BlockName, CallBlockName, StringComparison.OrdinalIgnoreCase);
                                else
                                {
                                    bool MatchesTarget(PlcInputAllowedUsage rule) => pin.BlockType == rule.CallPinTarget.TargetBlockType
                                        && (!rule.CallPinTarget.TargetBlockNumber.HasValue || pin.BlockNumber == rule.CallPinTarget.TargetBlockNumber)
                                        && (rule.CallPinTarget.TargetBlockName == null || pin.BlockName == null
                                            || string.Equals(pin.BlockName, rule.CallPinTarget.TargetBlockName, StringComparison.OrdinalIgnoreCase));
                                    wrongTarget = !AllowedUsages.Any(MatchesTarget);
                                    var matchingPairs = callerOptions!.Where(MatchesTarget).ToList();
                                    wrongPair = !wrongTarget && matchingPairs.Count == 0;
                                    if (matchingPairs.Count > 0)
                                    {
                                        var accessKnown = reference.AccessComplete && !inOutUnconfirmed
                                            && (IsRead(reference.Access) || IsWrite(reference.Access));
                                        var policy = matchingPairs.Select(rule => (
                                            DirectionMismatch: rule.CallPinTarget.Enabled == true && outputOrReturn,
                                            AccessMismatch: rule.FbReadOnly.Enabled == true && accessKnown && IsWrite(reference.Access) && !inOutAllowed,
                                            AccessUnconfirmed: rule.FbReadOnly.Enabled == true && !accessKnown))
                                            .OrderBy(p => (p.DirectionMismatch ? 1 : 0) + (p.AccessMismatch ? 1 : 0))
                                            .ThenBy(p => p.AccessUnconfirmed).First();
                                        wrongDirection = policy.DirectionMismatch;
                                        if (policy.AccessMismatch)
                                        {
                                            callerFail = rowCallerFail = true;
                                            pinViolations.Add(Violation("caller", "NOT_READ_ONLY", $"{site}: {reference.Access} is not read-only.", reference));
                                        }
                                        if (policy.AccessUnconfirmed)
                                        {
                                            callerComplete = rowCallerComplete = pinComplete = false;
                                            reasons.Add($"{site}: read-only access for this caller/callee pair could not be confirmed.");
                                        }
                                    }
                                }
                            }
                            var description = $"{site}: {pin.BlockType} {pin.BlockName} ({pin.BlockNumber}) pin {pin.Pin} ({pin.Direction}).";
                            if (wrongDirection) pinViolations.Add(Violation("callPin", "OUTPUT_OR_RETURN", description, reference));
                            if (wrongTarget) pinViolations.Add(Violation("callPin", "WRONG_CALLED_BLOCK", description, reference));
                            if (wrongPair) pinViolations.Add(Violation("callPin", "WRONG_CALL_PAIR",
                                $"{description} The caller and call target do not match the same allowed pair.", reference));
                            rowPinFail = wrongDirection || wrongTarget || wrongPair;
                            pinFail |= rowPinFail;
                            if (!pinUseComplete)
                            {
                                pinComplete = false;
                                reasons.Add($"{site}: call target or pin direction could not be confirmed.");
                            }
                            else if (!rowPinFail && (AllowedUsages == null || !rowCallerFail && rowCallerComplete))
                                hasTargetConnection = true;
                        }
                        var usePinStatus = RuleStatus(CallPinEnabled, rowPinFail, pinUseComplete);
                        pinAssessments.Add(AssessUse(rowCallerFail, rowCallerComplete, usePinStatus, pinUseComplete,
                            useViolations.Concat(pinViolations)));
                    }
                    var noPinStatus = CallPinEnabled && !excluded
                        && (!reference.ConfirmedUsage || !reference.CallPinTraceComplete || reference.Origin != "simaticML")
                        ? "INCOMPLETE" : "NOT_EVALUATED";
                    if (AllowedUsages != null && !excluded && pinAssessments.Count == 0 && !coveredNative.Contains(reference))
                    {
                        if (reference.Origin == "simaticML" && reference.ConfirmedUsage && reference.NetworkScanComplete
                            && reference.CallPinTraceComplete)
                        {
                            noPinStatus = "FAIL";
                            pinFail = true;
                            useViolations.Add(Violation("callPin", "MISSING_PAIRED_CALL_PIN",
                                $"{site}: this usage has no FB/FC pin connection to match an allowed pair.", reference));
                        }
                        else
                        {
                            noPinStatus = "INCOMPLETE";
                            pinComplete = false;
                            reasons.Add($"{site}: the pin connection for this usage could not be confirmed.");
                        }
                    }
                    assessments.Add(reference, (AssessUse(callerUseFail, callerUseComplete, noPinStatus,
                        noPinStatus != "INCOMPLETE", useViolations), pinAssessments));
                }
                if (CallPinEnabled && !excluded && hasUse && pinComplete && !hasTargetConnection)
                {
                    pinFail = true;
                    var message = AllowedUsages == null
                        ? $"No Input or InOut connection was found to {CallBlockType}{CallBlockNumber} {CallBlockName}."
                        : "No pin connection matched any allowed caller/callee pair.";
                    Violation("callPin", "MISSING_TARGET_INPUT", message);
                }

                var complete = excluded || (usageComplete && (!checkComment || !commentError)
                    && (!BlockEnabled || callerComplete) && (!CallPinEnabled || pinComplete) && (!GraphEnabled || graphComplete));
                anyIncomplete |= !complete;
                var callerStatus = RuleStatus(BlockEnabled, callerFail, callerComplete);
                var pinStatus = RuleStatus(CallPinEnabled, pinFail, pinComplete);
                var graphStatus = RuleStatus(GraphEnabled, graphFail, graphComplete);
                var status = excluded ? "EXCLUDED" : callerFail || pinFail || graphFail || commentFail ? "FAIL" : !complete ? "INCOMPLETE"
                    : rulesEnabled || checkComment ? "PASS" : "NOT_EVALUATED";
                counts[status]++;
                if (!excluded && hasUse) used++;
                if (status == "FAIL" && !complete) partial++;
                if (callerFail) callerFailures++;
                if (pinFail) pinFailures++;
                if (graphFail) graphFailures++;
                if (commentFail) commentFailures++;
                rows.Add(new JsonObject
                {
                    ["name"] = point.Name, ["address"] = point.Address, ["coveredAddress"] = point.Span.Canonical,
                    ["table"] = point.Table, ["softwareUnit"] = point.SoftwareUnit, ["comment"] = point.Comment.DeepClone(),
                    ["references"] = new JsonArray(references.Where(r => !coveredNative.Contains(r)).Select(reference =>
                    {
                        callers.TryGetValue(reference, out var caller);
                        var result = reference.ToJson(allowedInOut.Contains(reference), caller);
                        var assessment = assessments[reference];
                        result["assessment"] = assessment.Use;
                        var pins = (JsonArray)result["callPins"]!;
                        for (int i = 0; i < pins.Count; i++) pins[i]!["assessment"] = assessment.Pins[i];
                        return (JsonNode)result;
                    }).ToArray()),
                    ["callPinConnections"] = pinConnections, ["graphLocations"] = graphLocations,
                    ["status"] = status, ["safetyOnlyExcluded"] = false,
                    ["ruleStatus"] = callerStatus, ["callerRuleStatus"] = callerStatus,
                    ["callPinRuleStatus"] = pinStatus, ["graphDirectUseStatus"] = graphStatus,
                    ["complete"] = complete, ["hasSafetyUse"] = point.HasSafetyUse,
                    ["commentStatus"] = commentStatus,
                    ["reasons"] = Strings(violations.Select(v => v!["message"]!.GetValue<string>()).Concat(reasons).Distinct()),
                    ["uncertaintyReasons"] = Strings(reasons.Distinct()), ["violations"] = violations,
                });

                string RuleStatus(bool enabled, bool fail, bool ruleComplete) => !enabled || excluded ? "NOT_EVALUATED"
                    : fail ? "FAIL" : !ruleComplete ? "INCOMPLETE" : !hasUse ? "UNUSED" : "PASS";
            }

            var checkedCount = counts["PASS"] + counts["FAIL"] + counts["INCOMPLETE"] + counts["NOT_EVALUATED"];
            var allComplete = inventoryComplete && absoluteScanComplete && graphScanComplete
                && (!CallPinEnabled || callPinScanComplete) && !anyIncomplete;
            return new JsonObject
            {
                ["addressRanges"] = Strings(AddressRanges),
                ["configuration"] = new JsonObject
                {
                    ["commentFilterEnabled"] = CommentEnabled, ["commentKeywords"] = Strings(Keywords),
                    ["fbRuleEnabled"] = BlockEnabled, ["requiredBlockType"] = AllowedUsages == null ? BlockType : null,
                    ["requiredBlockNumber"] = AllowedUsages == null ? BlockNumber : (int?)null,
                    ["requiredLanguage"] = AllowedUsages == null ? Language : null,
                    ["callPinRuleEnabled"] = CallPinEnabled, ["requiredCallBlockType"] = AllowedUsages == null ? CallBlockType : null,
                    ["requiredCallBlockNumber"] = AllowedUsages == null ? CallBlockNumber : null,
                    ["requiredCallBlockName"] = AllowedUsages == null ? CallBlockName : null,
                    ["allowedUsages"] = AllowedUsages == null ? null : JsonSerializer.SerializeToNode(AllowedUsages),
                    ["graphTransitionRuleEnabled"] = GraphEnabled,
                    ["skipFbNumbers"] = new JsonArray(SkipFbNumbers.OrderBy(n => n).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                    ["skipFbNames"] = Strings(SkipFbNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                },
                ["rangeTagCount"] = tags, ["absoluteOnlyCount"] = absoluteOnly, ["excludedCount"] = counts["EXCLUDED"],
                ["usedCount"] = used, ["unusedCount"] = counts["UNUSED"], ["safetyOnlyExcludedCount"] = safetyOnlyExcluded,
                ["checkedInputCount"] = checkedCount, ["passCount"] = counts["PASS"], ["failCount"] = counts["FAIL"],
                ["unconfirmedCount"] = counts["INCOMPLETE"], ["partiallyUnconfirmedCount"] = partial,
                ["legacyUnconfirmedCount"] = counts["INCOMPLETE"] + partial, ["notEvaluatedCount"] = counts["NOT_EVALUATED"],
                ["ruleViolationCounts"] = new JsonObject
                {
                    ["caller"] = callerFailures, ["callPin"] = pinFailures, ["graphDirectUse"] = graphFailures,
                    ["comment"] = commentFailures,
                },
                ["points"] = rows, ["warnings"] = Strings(warnings), ["complete"] = allComplete,
                ["overallStatus"] = counts["FAIL"] > 0 ? "FAIL" : !allComplete ? "INCOMPLETE"
                    : checkedCount == 0 ? "UNUSED" : counts["PASS"] == 0 ? "NOT_EVALUATED" : "PASS",
            };
        }

        internal static JsonArray Strings(IEnumerable<string> values)
            => new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }
}
