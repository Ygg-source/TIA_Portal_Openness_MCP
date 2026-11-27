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

    internal sealed class PlcInputAuditReference
    {
        internal string? BlockPath { get; set; }
        internal string? BlockType { get; set; }
        internal int? BlockNumber { get; set; }
        internal string? Language { get; set; }
        internal string? Access { get; set; }
        internal string? Location { get; set; }
        internal string? Detail { get; set; }
        internal string? Address { get; set; }
        internal string Origin { get; set; } = "crossReference";
        internal bool Absolute { get; set; }
        internal bool ConfirmedUsage { get; set; } = true;
        internal int? Network { get; set; }

        internal JsonObject ToJson() => new JsonObject
        {
            ["blockPath"] = BlockPath, ["blockType"] = BlockType, ["blockNumber"] = BlockNumber,
            ["language"] = Language, ["access"] = Access, ["location"] = Location, ["detail"] = Detail,
            ["address"] = Address, ["origin"] = Origin, ["absolute"] = Absolute, ["confirmedUsage"] = ConfirmedUsage,
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
        internal List<PlcInputAuditReference> References { get; } = new List<PlcInputAuditReference>();
    }

    internal sealed class PlcInputUsageAudit
    {
        internal PlcInputUsageAudit(string[] addressRanges, bool commentEnabled, string[]? keywords,
            bool blockEnabled, string blockType, int blockNumber, string language)
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
        }

        private PlcInputSpan[] Ranges { get; }
        private string[] AddressRanges { get; }
        private bool CommentEnabled { get; }
        private string[] Keywords { get; }
        private bool BlockEnabled { get; }
        private string BlockType { get; }
        private int BlockNumber { get; }
        private string Language { get; }

        internal bool InRange(PlcInputSpan span) => Ranges.Any(span.Overlaps);
        internal bool MayContainStart(long start) => Ranges.Any(range => start <= range.End);

        internal static bool IsMetadata(string? type, string? access)
            => type is "Uses" or "TypeInstance" or "InstanceType" or "Assigns" or "MemberGroup" or "GroupMember"
                or "Defines" or "DefinedBy" or "OverlapsWith" or "Scope"
                || access is "Definition" or "Declaration" or "Interface" or "DefaultValue" or "ArrayBoundary" or "StringLength";

        internal static bool IsRead(string? access) => access is "Read" or "ReadAndSymbol";
        private static bool IsWrite(string? access) => access is "Write" or "WriteAndSymbol" or "RW" or "ReadWriteAndSymbol";

        internal static List<PlcInputAuditReference> DistinctReferences(IEnumerable<PlcInputAuditReference> references)
        {
            var distinct = references.GroupBy(r => (r.BlockPath, r.BlockType, r.BlockNumber, r.Language, r.Access,
                r.Location, r.Detail, r.Address, r.Origin, r.Absolute, r.ConfirmedUsage)).Select(g => g.First()).ToList();
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
            bool inventoryComplete, bool absoluteScanComplete)
        {
            var rows = new JsonArray();
            int used = 0, unused = 0, excluded = 0, passed = 0, failed = 0, unknown = 0;
            int tags = 0, absoluteOnly = 0;
            bool anyIncomplete = false;
            foreach (var point in points.Where(p => InRange(p.Span)).OrderBy(p => p.Span.Start).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (point.Table == null) absoluteOnly++;
                else tags++;
                var references = DistinctReferences(point.References);
                var confirmed = references.Where(r => r.ConfirmedUsage).ToList();
                var reasons = new List<string>();
                var violations = new List<string>();
                var commentError = point.Comment.ContainsKey("_error");
                var match = CommentEnabled && point.Comment.Any(c => c.Key != "_error"
                    && c.Value is JsonValue value && value.TryGetValue<string>(out var comment)
                    && Keywords.Any(k => comment.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
                if (CommentEnabled && commentError) reasons.Add("Multilingual comments could not be read completely.");
                if (!point.ReferencesComplete) reasons.Add("Native references could not be read completely.");
                if (!inventoryComplete) reasons.Add("The PLC inventory is incomplete.");
                if (!absoluteScanComplete) reasons.Add("The absolute-address scan is incomplete.");
                foreach (var r in references)
                {
                    var site = (r.BlockPath ?? "(unresolved user)") + " " + r.Location;
                    if (!r.ConfirmedUsage)
                    {
                        reasons.Add($"{site}: the usage could not be confirmed.");
                        continue;
                    }
                    if (!IsRead(r.Access) && !IsWrite(r.Access))
                        reasons.Add($"{site}: unconfirmed access ({r.Access ?? "undefined"}).");
                    if (!BlockEnabled) continue;
                    if (r.BlockType != null && r.BlockType != BlockType
                        || r.BlockNumber.HasValue && r.BlockNumber.Value != BlockNumber
                        || r.Language != null && !string.Equals(r.Language, Language, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{site}: expected {BlockType}{BlockNumber} {Language}, found {r.BlockType}{r.BlockNumber} {r.Language}.");
                    else if (r.BlockType == null || !r.BlockNumber.HasValue || r.Language == null)
                        reasons.Add($"{site}: the required block properties could not be confirmed.");
                    if (IsWrite(r.Access))
                        violations.Add($"{site}: {r.Access} is not read-only.");
                }

                var complete = reasons.Count == 0;
                anyIncomplete |= !complete;
                if (match) excluded++;
                else
                {
                    if (confirmed.Count > 0) used++;
                    if (complete && confirmed.Count == 0) unused++;
                    if (!complete) unknown++;
                    if (violations.Count > 0) failed++;
                    if (complete && confirmed.Count > 0 && BlockEnabled && violations.Count == 0) passed++;
                }

                var ruleStatus = !BlockEnabled || match ? "NOT_EVALUATED" : violations.Count > 0 ? "FAIL"
                    : !complete ? "INCOMPLETE" : confirmed.Count == 0 ? "UNUSED" : "PASS";
                var status = match ? "EXCLUDED" : !complete && violations.Count == 0 ? "INCOMPLETE"
                    : confirmed.Count == 0 ? "UNUSED" : ruleStatus;
                rows.Add(new JsonObject
                {
                    ["name"] = point.Name, ["address"] = point.Address, ["coveredAddress"] = point.Span.Canonical,
                    ["table"] = point.Table, ["softwareUnit"] = point.SoftwareUnit, ["comment"] = point.Comment,
                    ["references"] = new JsonArray(references.Select(r => (JsonNode)r.ToJson()).ToArray()),
                    ["status"] = status, ["ruleStatus"] = ruleStatus, ["complete"] = complete,
                    ["commentStatus"] = !CommentEnabled ? "NOT_EVALUATED" : match ? "EXCLUDED" : commentError ? "INCOMPLETE" : "PASS",
                    ["reasons"] = Strings(violations.Concat(reasons).Distinct()),
                });
            }

            var allComplete = inventoryComplete && absoluteScanComplete && !anyIncomplete;
            return new JsonObject
            {
                ["addressRanges"] = Strings(AddressRanges),
                ["configuration"] = new JsonObject
                {
                    ["commentFilterEnabled"] = CommentEnabled, ["commentKeywords"] = Strings(Keywords),
                    ["fbRuleEnabled"] = BlockEnabled, ["requiredBlockType"] = BlockType,
                    ["requiredBlockNumber"] = BlockNumber, ["requiredLanguage"] = Language,
                },
                ["rangeTagCount"] = tags, ["absoluteOnlyCount"] = absoluteOnly, ["excludedCount"] = excluded,
                ["usedCount"] = used, ["unusedCount"] = unused, ["passCount"] = passed, ["failCount"] = failed,
                ["unconfirmedCount"] = unknown, ["points"] = rows, ["warnings"] = Strings(warnings),
                ["complete"] = allComplete,
                ["overallStatus"] = !allComplete ? "INCOMPLETE" : failed > 0 ? "FAIL"
                    : !BlockEnabled ? "NOT_EVALUATED" : used == 0 ? "UNUSED" : "PASS",
            };
        }

        internal static JsonArray Strings(IEnumerable<string> values)
            => new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }
}
