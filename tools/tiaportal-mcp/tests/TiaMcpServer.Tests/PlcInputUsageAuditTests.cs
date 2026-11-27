using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class PlcInputUsageAuditTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var audit = Audit();
            check(audit.InRange(Span("%I0.0")) && audit.InRange(Span("%I2.7")) && !audit.InRange(Span("%I3.0"))
                && audit.InRange(Span("%I12.7")) && !audit.InRange(Span("%I13.0")), "I audit: disjoint and overlapping inclusive ranges");
            check(Span("%ID9").End - Span("%ID9").Start == 31 && Span("%IW4").End == Span("%I5.7").End
                && Span("%IB4").Start == Span("%I4.0").Start, "I audit: byte, word and double-word widths");
            foreach (var bad in new[] { "%I0.8", "%Q0.0", "%I0", "%ID2147483648", "%IB5..%IB4", "%I0.0..%I2.0..%I3.0" })
                check(!PlcInputSpan.TryRange(bad, out _), $"I audit: reject {bad}");

            var excluded = Point("Cyl", "%I0.0", Ref("FB", 82, "LAD", "Write"));
            excluded.Comment["de-DE"] = "ZyLiNdEr";
            var rows = audit.Summarize(new[] { excluded, Point("Empty", "%I0.1") }, Array.Empty<string>(), true, true);
            check(Status(Row(rows, "Cyl")) == "EXCLUDED" && Status(Row(rows, "Empty")) == "UNUSED",
                "I audit: any language, case-insensitive match excludes, empty comment does not");
            check(Status(Row(Report(Audit(comment: false), excluded), "Cyl")) == "FAIL", "I audit: comment switch acts independently");
            var badComment = Point("Comment", "%I0.1", Ref("FB", 81, "LAD", "Read"));
            badComment.Comment["_error"] = "unavailable";
            check(Status(Row(Report(audit, badComment), "Comment")) == "INCOMPLETE", "I audit: failed comment read cannot pass");
            check(Status(Row(Report(Audit(comment: false), badComment), "Comment")) == "PASS", "I audit: disabled comment rule does not gate a valid block rule");

            foreach (var access in new[] { "Read", "ReadAndSymbol", "Write", "WriteAndSymbol", "RW", "ReadWriteAndSymbol", "Unknown", "Undefined", "other" })
            {
                var p = Point("Tag", "%I0.0", Ref("FB", 81, "LAD", access));
                var expected = access is "Read" or "ReadAndSymbol" ? "PASS"
                    : access is "Write" or "WriteAndSymbol" or "RW" or "ReadWriteAndSymbol" ? "FAIL" : "INCOMPLETE";
                check(Status(Row(Report(audit, p), "Tag")) == expected, $"I audit: access={access}");
            }

            foreach (var r in new[] { Ref("FB", 82, "LAD", "Read"), Ref("FC", 81, "LAD", "Read"),
                Ref("OB", 81, "LAD", "Read"), Ref("FB", 81, "FBD", "Read"), Ref("FB", 81, "SCL", "Read") })
            {
                var p = Point("Tag", "%I0.0", Ref("FB", 81, "LAD", "Read"), r);
                var row = Row(Report(audit, p), "Tag");
                check(Status(row) == "FAIL" && row["references"]!.AsArray().Count == 2,
                    $"I audit: any violation among multiple uses ({r.BlockType}{r.BlockNumber} {r.Language})");
                var disabled = Row(Report(Audit(block: false), p), "Tag");
                check(Status(disabled) == "NOT_EVALUATED" && disabled["references"]!.AsArray().Count == 2,
                    "I audit: disabling the block rule returns every use");
            }

            var unit = Point("UnitTag", "%I0.0", Ref("FB", 81, "LAD", "Read"));
            unit.Table = "Software units/UnitA/Tags";
            unit.SoftwareUnit = "UnitA";
            unit.References[0].BlockPath = "Software units/UnitA/Blocks/Block";
            unit.References.Add(unit.References[0]);
            var unitRow = Row(Report(audit, unit), "UnitTag");
            check(unitRow["softwareUnit"]!.GetValue<string>() == "UnitA" && unitRow["references"]!.AsArray().Count == 1,
                "I audit: unit scope and repeated native reference deduplication");

            var unread = Point("Unread", "%I0.0");
            unread.ReferencesComplete = false;
            check(Status(Row(Report(audit, unread), "Unread")) == "INCOMPLETE", "I audit: failed native read cannot yield UNUSED");
            var used = Point("Used", "%I0.0", Ref("FB", 81, "LAD", "Read"));
            var partial = audit.Summarize(new[] { used }, new[] { "export refused" }, true, false);
            check(!partial["complete"]!.GetValue<bool>() && Status(Row(partial, "Used")) == "INCOMPLETE",
                "I audit: incomplete absolute scan rules out PASS");
            check(PlcInputUsageAudit.IsMetadata("DefinedBy", "Read") && PlcInputUsageAudit.IsMetadata("UsedBy", "Declaration")
                && !PlcInputUsageAudit.IsMetadata("UsedBy", "ReadAndSymbol"), "I audit: separate metadata from executable uses");

            var large = Enumerable.Range(0, 5101).Select(i => Point($"Tag{i}", $"%I{i}.0"));
            var largeReport = Report(new PlcInputUsageAudit(new[] { "%I0.0..%I5100.7" }, false, null, true, "FB", 81, "LAD"), large.ToArray());
            check(largeReport["rangeTagCount"]!.GetValue<int>() == 5101 && largeReport["unusedCount"]!.GetValue<int>() == 5101,
                "I audit: full inventory above the GetPlcTags result cap");

            ScanAbsolute(check);
            check(ToolSafety.Classify("AuditPlcInputUsage").ReadOnly, "I audit: read-only policy");
        }

        private static void ScanAbsolute(Action<bool, string> check)
        {
            const string flgNet = """
                <FlgNet xmlns="urn:test:flg">
                  <Parts>
                    <Access Scope="Address" UId="1"><Address Area="Input" Type="Bool" BitOffset="0"/></Access>
                    <Access Scope="Address" UId="2"><Address Area="Input" Type="Bool" BitOffset="1"/></Access>
                    <Access Scope="Address" UId="3"><Address Area="Input" Type="Word" BitOffset="16"/></Access>
                    <Access Scope="GlobalVariable" UId="4"><Symbol><Address Area="Input" Type="Bool" BitOffset="3" Informative="true"/></Symbol></Access>
                    <Part UId="5" Name="Contact"/><Part UId="6" Name="Coil"/>
                    <Call UId="7"><CallInfo><Parameter Name="p" Section="InOut"/></CallInfo></Call>
                  </Parts>
                  <Wires>
                    <Wire><IdentCon UId="1"/><NameCon UId="5" Name="operand"/></Wire>
                    <Wire><IdentCon UId="2"/><NameCon UId="6" Name="operand"/></Wire>
                    <Wire><IdentCon UId="3"/><NameCon UId="7" Name="p"/></Wire>
                  </Wires>
                </FlgNet>
                """;
            const string scl = """
                <StructuredText xmlns="urn:test:scl">
                  <Access Scope="Address" UId="1"><Address Area="Input" Type="Byte" BitOffset="8"/></Access>
                  <Blank/><Token Text=":="/>
                  <Access Scope="Address" UId="2"><Address Area="Input" Type="Byte" BitOffset="16"/></Access>
                  <Token Text=";"/>
                  <Access Scope="Call"><Instruction><Parameter Name="unknown">
                    <Access Scope="Address"><Address Area="Input" Type="Bool" BitOffset="31"/></Access>
                  </Parameter></Instruction></Access>
                  <LineComment><Text>%I1234.0</Text></LineComment>
                </StructuredText>
                """;
            var issues = new List<string>();
            var uses = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(flgNet) + Unit(scl) + "</Document>"), issues);
            check(issues.Count == 0 && uses.Select(u => u.Access).SequenceEqual(new[] { "Read", "Write", "RW", "Write", "Read", "Unknown" }),
                "I audit: LAD/FBD wired operands and SCL statement context, skip informative addresses and comments");
            check(uses[2].Span.Overlaps(Span("%I3.7")) && uses[2].Span.Canonical == "%IW2",
                "I audit: associate wide absolute operand with overlapping tagged input");
            var native = Ref("FB", 81, "LAD", "Read");
            native.Absolute = true;
            native.Address = uses[0].Span.Canonical;
            native.Network = 1;
            var xml = Ref("FB", 81, "LAD", "Read");
            xml.Absolute = true;
            xml.Address = native.Address;
            xml.Network = 1;
            xml.Origin = "simaticML";
            xml.Location = uses[0].Site;
            var symbolic = Ref("FB", 81, "LAD", "ReadAndSymbol");
            check(PlcInputUsageAudit.DistinctReferences(new[] { native, xml, symbolic }).SequenceEqual(new[] { xml, symbolic }),
                "I audit: reconcile the same native/XML absolute site without dropping a symbolic use");
            issues.Clear();
            PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit("<Graph/>") + "</Document>"), issues);
            check(issues.Count > 0, "I audit: unsupported network must surface a warning");
        }

        private static string Unit(string body)
            => "<SW.Blocks.CompileUnit><AttributeList><NetworkSource>" + body + "</NetworkSource></AttributeList></SW.Blocks.CompileUnit>";

        private static PlcInputUsageAudit Audit(bool comment = true, bool block = true)
            => new PlcInputUsageAudit(new[] { "%I0.0..%I1.7", "%IB1..%IB2", "%IB12" }, comment,
                new[] { "气缸", "zylinder" }, block, "FB", 81, "LAD");

        private static PlcInputSpan Span(string address)
            => PlcInputSpan.TryParse(address, out var span) ? span : throw new ArgumentException(address);

        private static PlcInputAuditPoint Point(string name, string address, params PlcInputAuditReference[] references)
        {
            var point = new PlcInputAuditPoint { Name = name, Table = "Inputs", Address = address, Span = Span(address) };
            point.References.AddRange(references);
            return point;
        }

        private static PlcInputAuditReference Ref(string type, int number, string language, string access)
            => new PlcInputAuditReference
            {
                BlockPath = $"Program blocks/{type}{number}", BlockType = type, BlockNumber = number,
                Language = language, Access = access, Location = "NW1",
            };

        private static JsonObject Report(PlcInputUsageAudit audit, params PlcInputAuditPoint[] points)
            {
            foreach (var point in points) point.Comment = (JsonObject)point.Comment.DeepClone();
            return audit.Summarize(points, Array.Empty<string>(), true, true);
        }

        private static JsonNode Row(JsonObject report, string name)
            => report["points"]!.AsArray().Single(p => p?["name"]?.GetValue<string>() == name)!;

        private static string Status(JsonNode row) => row["status"]!.GetValue<string>();
    }
}
