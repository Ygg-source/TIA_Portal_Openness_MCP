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
            CheckCallPins(check);
            CheckReferenceSources(check);
            CheckGraph(check);
            CheckSafetyAndCounts(check);
            CheckFixedExclusions(check);
            CheckStatementList(check);
            check(ToolSafety.Classify("AuditPlcInputUsage").ReadOnly, "I audit: read-only policy");
        }

        private static void CheckCallPins(Action<bool, string> check)
        {
            const string flg = """
                <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
                  <Parts>
                    <Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="Input.Tag"/></Symbol></Access>
                    <Access Scope="Address" UId="2"><Address Area="Input" Type="Bool" BitOffset="1"/></Access>
                    <Call UId="3"><CallInfo Name="Target" BlockType="FB">
                      <Instance Scope="GlobalVariable" UId="6"><Component Name="TargetInstance"/></Instance>
                      <Parameter Name="a" Section="Input"/><Parameter Name="b" Section="InOut"/>
                    </CallInfo></Call>
                    <Call UId="4"><CallInfo Name="Peer" BlockType="FC">
                      <Parameter Name="a" Section="Input"/><Parameter Name="b" Section="InOut"/>
                      <Parameter Name="c" Section="Output"/><Parameter Name="r" Section="Return"/>
                    </CallInfo></Call>
                    <Part Name="Contact" UId="5"/>
                  </Parts>
                  <Wires>
                    <Wire><IdentCon UId="1"/><NameCon UId="3" Name="a"/><NameCon UId="4" Name="a"/></Wire>
                    <Wire><IdentCon UId="1"/><NameCon UId="3" Name="b"/></Wire>
                    <Wire><IdentCon UId="1"/><NameCon UId="5" Name="operand"/></Wire>
                    <Wire><IdentCon UId="2"/><NameCon UId="4" Name="b"/></Wire>
                    <Wire><NameCon UId="4" Name="c"/><IdentCon UId="2"/></Wire>
                    <Wire><NameCon UId="4" Name="r"/><IdentCon UId="2"/></Wire>
                    <Wire><IdentCon UId="2"/><NameCon UId="4" Name="unknown"/></Wire>
                  </Wires>
                </FlgNet>
                """;
            var issues = new List<string>();
            var uses = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(flg) + "</Document>"), issues);
            var symbolic = uses.Single(u => u.Symbol != null);
            var absolute = uses.Single(u => u.Absolute);
            check(issues.Count == 0 && symbolic.Symbol!.SequenceEqual(new[] { "Input.Tag" }) && symbolic.Pins.Count == 3
                && symbolic.Access == "RW" && absolute.Span.Canonical == "%I0.1" && absolute.Pins.Count == 4,
                "I audit: preserve every call and pin in the network for scoped symbols and absolute operands");
            check(symbolic.Pins[0].BlockName == "Target" && symbolic.Pins[0].InstanceName == "TargetInstance"
                && symbolic.Pins[0].InstanceScope == "GlobalVariable", "I audit: distinguish FB type and instance");
            var sameOperandWrite = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(flg.Replace("Contact", "Coil")) + "</Document>"), issues);
            check(!symbolic.HasOtherWrite && sameOperandWrite.Single(u => u.Symbol != null).HasOtherWrite,
                "I audit: identify ordinary write wires beside a direct InOut connection");
            var mixedNetworkIssues = new List<string>();
            var mixedNetworks = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(flg) + Unit("<StatementList/>") + "</Document>"), mixedNetworkIssues);
            check(mixedNetworkIssues.Count == 1 && mixedNetworks.All(u => u.NetworkScanComplete),
                "I audit: an unsupported later network does not invalidate parsed InOut evidence");
            foreach (var (section, kind) in new[] { ("Static", "MultiInstance"), ("InOut", "ParameterInstance") })
            {
                var local = flg.Replace("Instance Scope=\"GlobalVariable\"", "Instance Scope=\"LocalVariable\"");
                var xml = $"<SW.Blocks.FB><AttributeList><Interface><Sections><Section Name=\"{section}\">"
                    + """<Member Name="TargetInstance" Datatype="&quot;Target&quot;"/>"""
                    + "</Section></Sections></Interface></AttributeList>" + Unit(local) + "</SW.Blocks.FB>";
                var pin = PlcInputSimaticMl.Scan(XDocument.Parse(xml), issues).First().Pins.First();
                check(pin.InstanceKind == kind && pin.BlockName == "Target" && pin.InstanceTypeName == "\"Target\"",
                    $"I audit: resolve {kind} using its caller interface declaration");
            }
            foreach (var pin in symbolic.Pins.Concat(absolute.Pins)) pin.BlockNumber = pin.BlockType == "FB" ? 82 : 7;

            foreach (var pin in symbolic.Pins.Concat(absolute.Pins).Where(p => p.Direction is "Input" or "InOut"))
            {
                var use = Ref("FB", 81, "LAD", pin.Direction == "InOut" ? "RW" : "Read");
                use.Origin = "simaticML";
                use.CallPins.Add(pin);
                var point = Point("Tag", "%I0.0", use, Ref("FB", 81, "LAD", "Read"));
                var row = Row(Report(AuditRules(pin.BlockType, pin.BlockNumber, pin.BlockName, caller: true), point), "Tag");
                check(row["callPinRuleStatus"]!.ToString() == "PASS"
                    && row["callerRuleStatus"]!.ToString() == "PASS",
                    $"I audit: {pin.BlockType} {pin.Direction} is accepted by pin rule independently of caller access");
                var independent = Row(Report(AuditRules(pin.BlockType, pin.BlockNumber, pin.BlockName), point), "Tag");
                check(Status(independent) == "PASS" && independent["callerRuleStatus"]!.ToString() == "NOT_EVALUATED",
                    "I audit: disabling caller rule preserves the pin decision");
            }
            var directInOut = Ref("FB", 81, "LAD", "RW");
            directInOut.Origin = "simaticML";
            directInOut.Symbol = "Tag";
            directInOut.SymbolComponents = new[] { "Tag" };
            directInOut.Network = 1;
            directInOut.NetworkScanComplete = true;
            directInOut.CallPins.Add(symbolic.Pins.Single(p => p.Direction == "InOut"));
            var nativeInOut = Ref("FB", 81, "LAD", "ReadWriteAndSymbol");
            nativeInOut.Network = 1;
            var inOutPoint = Point("Tag", "%I0.0", nativeInOut, directInOut);
            var callerOnly = AuditRules(null, null, null, pins: false, caller: true);
            check(Status(Row(Report(callerOnly, inOutPoint), "Tag")) == "PASS",
                "I audit: reconcile a native read/write reference with the verified InOut operation in the same network");
            directInOut.Symbol = "Tag.Field";
            directInOut.SymbolComponents = new[] { "Tag", "Field" };
            nativeInOut.Symbol = "\"Tag\".\"Field\"";
            check(Status(Row(Report(callerOnly, inOutPoint), "Tag")) == "PASS",
                "I audit: reconcile a quoted native field with its exact XML symbol components");
            directInOut.NetworkScanComplete = false;
            check(Status(Row(Report(callerOnly, inOutPoint), "Tag")) == "INCOMPLETE",
                "I audit: unverified native attribution is not a confirmed write violation");
            directInOut.NetworkScanComplete = true;
            directInOut.HasOtherWrite = true;
            check(Status(Row(Report(callerOnly, inOutPoint), "Tag")) == "FAIL",
                "I audit: an ordinary write on the same operand remains a violation");
            directInOut.HasOtherWrite = false;
            nativeInOut.Network = 2;
            check(Status(Row(Report(callerOnly, inOutPoint), "Tag")) == "FAIL",
                "I audit: InOut in one network does not waive a write in another network");
            foreach (var pin in absolute.Pins.Where(p => p.Direction is not ("Input" or "InOut")))
            {
                var use = Ref("FB", 81, "LAD", "Read");
                use.Origin = "simaticML";
                use.CallPins.Add(pin);
                var row = Row(Report(AuditRules("FC", 7, "Peer"), Point("Tag", "%I0.1", use)), "Tag");
                check(Status(row) == (pin.Direction == null ? "INCOMPLETE" : "FAIL"), "I audit: Output, Return and unknown directions");
            }
            var fb = Ref("FB", 81, "LAD", "Read");
            fb.Origin = "simaticML";
            fb.CallPins.Add(symbolic.Pins[0]);
            var tag = Point("Tag", "%I0.0", fb);
            foreach (var audit in new[] { AuditRules("FC", 82, "Target"), AuditRules("FB", 83, "Target"), AuditRules("FB", 82, "Other") })
                check(Status(Row(Report(audit, tag), "Tag")) == "FAIL", "I audit: call type, number and name all required");
            fb.CallPins.Add(symbolic.Pins[1]);
            check(Status(Row(Report(AuditRules("FB", 82, "Target"), tag), "Tag")) == "FAIL", "I audit: correct and wrong target on same input fail");
            check(Status(Row(Report(AuditRules(null, null, null, pins: false, caller: true), tag), "Tag")) == "PASS",
                "I audit: disabled pin rule does not affect caller result");
            check(Status(Row(Report(AuditRules("FB", 82, "Target"), Point("Contact", "%I0.0", Ref("FB", 81, "LAD", "Read"))), "Contact")) == "FAIL",
                "I audit: complete scan with only a contact has no required pin");
            bool invalid = false;
            try { AuditRules("OB", 0, null); }
            catch (ArgumentException) { invalid = true; }
            check(invalid, "I audit: enabled call-pin rule rejects missing or invalid target parameters");

            const string scl = """
                <StructuredText><Access Scope="Call"><CallInfo BlockType="FC">
                  <Instance Scope="GlobalVariable"><Component Name="DeviceStatus"/></Instance><Token Text="("/>
                  <Parameter Name="value"><Token Text="=&gt;"/>
                    <Access Scope="Address"><Address Area="Input" Type="Bool" BitOffset="1"/></Access>
                  </Parameter><Token Text=")"/>
                </CallInfo></Access><Token Text=";"/></StructuredText>
                """;
            var sclUse = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(scl) + "</Document>"), issues).Single();
            check(sclUse.Confirmed && sclUse.Span.Canonical == "%I0.1" && sclUse.Access == "Write"
                && sclUse.Pins.Single().Direction == "Output" && sclUse.Pins.Single().BlockName == "DeviceStatus",
                "I audit: direct SCL output to an absolute input with a V20 FC call encoded as Instance");
            var expression = scl.Replace("=&gt;", ":=").Replace("</Parameter>", """
                <Token Text="OR"/><Access Scope="GlobalVariable">
                  <Symbol><Component Name="Intermediate"/></Symbol>
                </Access></Parameter>
                """);
            var expressionUses = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(expression) + "</Document>"), issues);
            check(expressionUses.Any(u => u.Absolute && u.Confirmed) && expressionUses.All(u => u.Pins.Count == 0),
                "I audit: SCL expression operands are uses but do not count as direct pin connections");
            var callee = XDocument.Parse("""
                <Document><SW.Blocks.FC><AttributeList><Interface><Sections>
                  <Section Name="InOut"><Member Name="value" Datatype="Bool"/></Section>
                </Sections></Interface></AttributeList></SW.Blocks.FC></Document>
                """);
            check(PlcInputSimaticMl.ReadPinDirections(callee)["value"].Single() == "InOut",
                "I audit: use callee interface to distinguish SCL InOut from Input when the parameter carries no Section");
        }

        private static void CheckGraph(Action<bool, string> check)
        {
            const string operand = """<Access UId="1" Scope="Address"><Address Area="Input" Type="Bool" BitOffset="0"/></Access>""";
            const string symbol = """<Access UId="1" Scope="GlobalVariable"><Symbol><Component Name="DirectInput"/></Symbol></Access>""";
            const string contact = """<Part UId="2" Name="Contact"/>""";
            const string call = """<Call UId="2"><CallInfo BlockType="FC" Name="Target"><Parameter Name="in" Section="Input"/></CallInfo></Call>""";
            string Net(string access, string part, string pin) => "<FlgNet><Parts>" + access + part
                + $$"""</Parts><Wires><Wire><IdentCon UId="1"/><NameCon UId="2" Name="{{pin}}"/></Wire></Wires></FlgNet>""";
            var graph = """<Graph xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6">"""
                + "<PreOperations><PermanentOperation>" + Net(operand, contact, "operand") + "</PermanentOperation></PreOperations>"
                + "<Sequence><Steps><Step Number=\"1\"><Actions><Action>" + operand + "</Action></Actions></Step></Steps>"
                + "<Transitions><Transition Name=\"Ready\" Number=\"42\">" + Net(symbol, contact, "operand")
                + """</Transition><Transition Name="Intermediate" Number="43">"""
                + Net(symbol.Replace("DirectInput", "Intermediate"), contact, "operand")
                + "</Transition></Transitions></Sequence><PostOperations><PermanentOperation>" + Net(operand, call, "in")
                + "</PermanentOperation></PostOperations></Graph>";
            var issues = new List<string>();
            var uses = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(graph) + "</Document>"), issues);
            var direct = uses.Where(u => u.Absolute || u.Symbol!.Single() == "DirectInput").ToList();
            check(issues.Count == 0 && direct.Select(u => u.GraphRegion).SequenceEqual(new[]
                { "PermanentPreInstructions", "PermanentPostInstructions", "Transition", null })
                && direct[1].Pins.Single().Direction == "Input" && direct[2].TransitionNumber == 42,
                "I audit: V20 GRAPH regions and call operands, exclude indirect intermediate and step action from GRAPH violations");
            var point = Point("DirectInput", "%I0.0", direct.Select(u =>
            {
                var reference = Ref("FB", 81, "GRAPH", u.Access);
                reference.Location = u.Site;
                reference.GraphRegion = u.GraphRegion;
                reference.TransitionNumber = u.TransitionNumber;
                return reference;
            }).ToArray());
            var graphAudit = AuditRules(null, null, null, pins: false, graph: true);
            var row = Row(Report(graphAudit, point), "DirectInput");
            check(Status(row) == "FAIL" && row["graphLocations"]!.AsArray().Count == 3, "I audit: GRAPH returns three positions for one input");
            check(Status(Row(Report(graphAudit, Point("Step", "%I0.1", point.References.Last())), "Step")) == "PASS",
                "I audit: step use is outside GRAPH rule");
            check(Status(Row(Report(graphAudit, Point("NoGraph", "%I0.0", Ref("FB", 81, "LAD", "Read"))), "NoGraph")) == "PASS",
                "I audit: a project without GRAPH can complete the GRAPH rule");
            var brokenGraph = graph.Replace("Number=\"42\"", "Number=\"invalid\"");
            var broken = PlcInputSimaticMl.Scan(XDocument.Parse("<Document>" + Unit(brokenGraph) + "</Document>"), issues);
            check(issues.Count != 0 && broken.Any(u => u.GraphRegion == "PermanentPostInstructions"),
                "I audit: malformed GRAPH region is reported without losing other positions");
            var partial = graphAudit.Summarize(new[] { point }, issues, true, true, graphScanComplete: false);
            check(Status(Row(partial, "DirectInput")) == "FAIL" && !partial["complete"]!.GetValue<bool>(),
                "I audit: keep GRAPH violation with partial coverage");
        }

        private static void CheckSafetyAndCounts(Action<bool, string> check)
        {
            var blocks = new[] { ("LAD", false), ("F_LAD", false), ("F_FBD", false), ("F_DB", false),
                ("F_CALL", false), ("SCL", true), ("Undef", false) };
            var kinds = blocks.Select(b => PlcInputUsageAudit.ClassifyProgram(b.Item1, b.Item2)).ToArray();
            var exports = kinds.Where(kind => PlcInputUsageAudit.CanExport(kind, true)).ToList();
            check(exports.SequenceEqual(new[] { PlcInputProgramKind.Standard }) && kinds[6] == PlcInputProgramKind.Unknown,
                "I audit: only standard code can reach export, including standard code on an F-CPU");
            var used = Point("Mixed", "%I0.0", Ref("FC", 7, "GRAPH", "RW"), Ref("FC", 7, "GRAPH", "Read"));
            used.HasSafetyUse = true;
            used.References[0].GraphRegion = "Transition";
            var only = Point("SafetyOnly", "%I0.1");
            only.HasSafetyUse = true;
            var incomplete = Point("Unresolved", "%I0.2");
            incomplete.ReferencesComplete = false;
            incomplete.ReferenceIssues.Add("NW4 identity is unavailable.");
            var audit = AuditRules("FB", 82, "Target", caller: true, graph: true);
            var report = Report(audit, used, only, incomplete);
            check(Row(report, "Unresolved")["uncertaintyReasons"]!.AsArray().Any(r => r!.ToString() == "NW4 identity is unavailable."),
                "I audit: preserve the concrete uncertainty for the point");
            check(report["checkedInputCount"]!.GetValue<int>() == 2 && report["failCount"]!.GetValue<int>() == 1
                && report["unconfirmedCount"]!.GetValue<int>() == 1 && report["safetyOnlyExcludedCount"]!.GetValue<int>() == 1
                && report["ruleViolationCounts"]!["caller"]!.GetValue<int>() == 1 && report["ruleViolationCounts"]!["graphDirectUse"]!.GetValue<int>() == 1
                && Row(report, "SafetyOnly")["complete"]!.GetValue<bool>() && Row(report, "SafetyOnly")["safetyOnlyExcluded"]!.GetValue<bool>()
                && Status(Row(report, "SafetyOnly")) == "UNUSED", "I audit: exclusive safety-only count with the legacy row status");
            used.ReferencesComplete = false;
            report = Report(audit, used);
            check(report["failCount"]!.GetValue<int>() == 1 && report["unconfirmedCount"]!.GetValue<int>() == 0
                && report["partiallyUnconfirmedCount"]!.GetValue<int>() == 1 && report["legacyUnconfirmedCount"]!.GetValue<int>() == 1,
                "I audit: a failed point with partial evidence is only one checked input");
            report = AuditRules(null, null, null, pins: false).Summarize(new[] { only }, Array.Empty<string>(), false, true);
            check(Status(Row(report, "SafetyOnly")) == "INCOMPLETE", "I audit: unclassified inventory precludes a safety-only conclusion");
            report = Report(AuditRules(null, null, null, pins: false), Point("Used", "%I0.0", Ref("FB", 81, "LAD", "Read")));
            check(report["notEvaluatedCount"]!.GetValue<int>() == 1 && report["passCount"]!.GetValue<int>() == 0,
                "I audit: all usage rules disabled");
            var skipAudit = new PlcInputUsageAudit(new[] { "%I0.0" }, false, null, false, "FB", 81, "LAD",
                skipFbNumbers: new[] { 8, 81 }, skipFbNames: new[] { "Named FB", "AnotherFB" });
            check(skipAudit.SkipBlock("FB", 81, "Different") && skipAudit.SkipBlock("FB", 900, "named fb")
                && skipAudit.SkipBlock("FB", 901, "AnotherFB") && !skipAudit.SkipBlock("FC", 81, "Named FB")
                && !skipAudit.SkipBlock("FB", 901, "Other"), "I audit: skip assigned FB numbers or exact names only");
        }

        private static void CheckFixedExclusions(Action<bool, string> check)
        {
            var protectedBlock = PlcInputUsageAudit.ClassifyProgram("LAD", false, "FB", knowHowProtected: true);
            var failedProtection = PlcInputUsageAudit.ClassifyProgram("LAD", false, "FB", knowHowProtected: null);
            var proDiagFb = PlcInputUsageAudit.ClassifyProgram("ProDiag", false, "FB");
            var proDiagOb = PlcInputUsageAudit.ClassifyProgram("ProDiag_OB", false, "OB");
            var system = PlcInputUsageAudit.ClassifyProgram("LAD", false, "FB", systemBlock: true);
            var safety = PlcInputUsageAudit.ClassifyProgram("F_LAD", false, "FB");
            check(protectedBlock == PlcInputProgramKind.Protected && failedProtection == PlcInputProgramKind.Unknown
                && !PlcInputUsageAudit.CanExport(protectedBlock, true) && !PlcInputUsageAudit.CanExport(failedProtection, true),
                "I audit: know-how protected blocks and failed protection reads are excluded before export");
            check(proDiagFb == PlcInputProgramKind.ProDiag && proDiagOb == PlcInputProgramKind.ProDiag
                && !PlcInputUsageAudit.CanExport(proDiagFb, true) && !PlcInputUsageAudit.CanExport(proDiagOb, true)
                && PlcInputUsageAudit.ClassifyProgram("ProDiag", false, "OB") == PlcInputProgramKind.Unknown
                && new[] { ("LAD", "FB"), ("SCL", "OB"), ("S7_PDIAG", "FB") }.All(b =>
                    PlcInputUsageAudit.CanExport(PlcInputUsageAudit.ClassifyProgram(b.Item1, false, b.Item2), true)),
                "I audit: exact V20 ProDiag language and FB/OB identity, keep ordinary blocks and S7_PDIAG");
            check(system == PlcInputProgramKind.System && !PlcInputUsageAudit.CanExport(system, true)
                && safety == PlcInputProgramKind.Safety && !PlcInputUsageAudit.CanExport(safety, true),
                "I audit: system group membership and safety programs prevent export");

            var filtered = Point("Filtered", "%I0.0");
            var mixed = Point("Mixed", "%I0.1");
            var unknown = Point("Unknown", "%I0.2");
            var excludedUse = Ref("FB", 82, "SCL", "Write");
            excludedUse.BlockPath = "Technology objects/Axis";
            foreach (var kind in new[] { protectedBlock, proDiagFb, proDiagOb, system, PlcInputProgramKind.TechnologyObject })
                PlcInputUsageAudit.AddNativeProgramUse(filtered, excludedUse, kind, false);
            PlcInputUsageAudit.AddNativeProgramUse(mixed, excludedUse, PlcInputProgramKind.TechnologyObject, false);
            PlcInputUsageAudit.AddNativeProgramUse(mixed, Ref("FB", 81, "LAD", "Read"), PlcInputProgramKind.Standard, false);
            PlcInputUsageAudit.AddNativeProgramUse(unknown, excludedUse, PlcInputProgramKind.Unknown, false);
            var report = Report(Audit(), filtered, mixed, unknown);
            check(Status(Row(report, "Filtered")) == "UNUSED" && filtered.ReferencesComplete && filtered.ReferenceIssues.Count == 0
                && Status(Row(report, "Mixed")) == "PASS" && mixed.References.Count == 1 && mixed.ReferencesComplete
                && Status(Row(report, "Unknown")) == "INCOMPLETE" && unknown.ReferenceIssues.Count == 1
                && report["checkedInputCount"]!.GetValue<int>() == 2,
                "I audit: exclude technology object references without hiding unknown non-block references or ordinary uses");
        }

        private static void CheckStatementList(Action<bool, string> check)
        {
            const string address = """<Access Scope="Address"><Address Area="Input" Type="Bool" BitOffset="1"/></Access>""";
            const string symbol = """<Access Scope="GlobalVariable"><Symbol><Component Name="Input.Tag"/></Symbol></Access>""";
            string Instruction(string mnemonic, string operand = "") => $"""<StlStatement><StlToken Text="{mnemonic}"/>{operand}</StlStatement>""";
            string Stl(string statements) => """<StatementList xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StatementList/v5">"""
                + statements + "</StatementList>";
            XDocument Document(params string[] networks) => XDocument.Parse("<Document>" + string.Concat(networks.Select(Unit)) + "</Document>");
            var reads = new[] { "A", "AN", "O", "ON", "L", "X", "XN" };
            var writes = new[] { "Assign", "=", "S", "R", "T" };
            var noOperands = Instruction("SET", "<LineComment><Text>A %I100.0</Text></LineComment>") + Instruction("SAVE")
                + Instruction("L", """<Access Scope="TypedConstant"><Constant><ConstantType>String</ConstantType><ConstantValue>'%I100.1'</ConstantValue></Constant></Access>""");
            var issues = new List<string>();
            var stlNetworks = new HashSet<int>();
            var uses = PlcInputSimaticMl.Scan(Document("<FlgNet><Parts/></FlgNet>", Stl(noOperands +
                string.Concat(reads.Select(m => Instruction(m, symbol)))), Stl(
                string.Concat(writes.Select(m => Instruction(m, address))) + Instruction("Rise", address))), issues,
                stlNetworks: stlNetworks);
            check(issues.Count == 0 && stlNetworks.SetEquals(new[] { 2, 3 }) && uses.Count == reads.Length + writes.Length + 1
                && uses.Take(reads.Length).All(u => u.Access == "Read" && u.Network == 2 && u.Confirmed
                    && u.Symbol!.SequenceEqual(new[] { "Input.Tag" }))
                && uses.Skip(reads.Length).Take(writes.Length).All(u => u.Access == "Write" && u.Network == 3
                    && u.Confirmed && u.Absolute && u.Span.Canonical == "%I0.1")
                && uses.Last().Access == "RW", "I audit: STL mnemonic access, global and absolute operands in mixed networks and no access for SET, SAVE, comments or strings");

            string Parameter(string name, string section, string operand) => $"""<Parameter Name="{name}" Section="{section}">{operand}</Parameter>""";
            string Call(string type, int number, string name, string parameters) => Instruction("CALL", $"""
                <Access Scope="Call"><CallInfo Name="{name}" BlockType="{type}">
                  <IntegerAttribute Name="BlockNumber" Informative="true">{number}</IntegerAttribute>
                  {parameters}
                </CallInfo></Access>
                """);
            var calls = PlcInputSimaticMl.Scan(Document(Stl(
                Call("FB", 82, "Target", Parameter("in", "Input", symbol) + Parameter("io", "InOut", address))
                + Call("FC", 7, "Peer", Parameter("out", "Output", address) + Parameter("ret", "Return", symbol)))), issues);
            check(issues.Count == 0 && calls.Select(u => u.Access).SequenceEqual(new[] { "Read", "RW", "Write", "Write" })
                && calls.Select(u => u.Pins.Single().Direction).SequenceEqual(new[] { "Input", "InOut", "Output", "Return" })
                && calls.All(u => u.Pins.Single().Confirmed) && calls[0].Pins[0].BlockName == "Target"
                && calls[0].Pins[0].BlockNumber == 82 && calls[0].Pins[0].BlockType == "FB" && calls[1].Pins[0].Pin == "io"
                && calls[2].Pins[0].BlockName == "Peer" && calls[2].Pins[0].BlockNumber == 7 && calls[2].Pins[0].BlockType == "FC",
                "I audit: STL FB/FC call targets and distinct parameter directions");
            var numericCall = Instruction("CALL", $$"""
                <Access Scope="Call"><CallInfo BlockType="FC">
                    <Instance Scope="Address"><Address Area="FC" BlockNumber="7"/></Instance>
                    {{Parameter("value", "Input", """<Token Text="%IW2"/>""")}}
                </CallInfo></Access>
                """);
            var numeric = PlcInputSimaticMl.Scan(Document(Stl(numericCall)), issues).Single();
            check(issues.Count == 0 && numeric.Span.Canonical == "%IW2" && numeric.Pins[0].BlockType == "FC"
                && numeric.Pins[0].BlockNumber == 7 && numeric.Pins[0].BlockName == null,
                "I audit: numeric STL call target and absolute operand token");
            PlcInputAuditReference Reference(PlcInputXmlUse use)
            {
                var reference = Ref("FB", 81, "STL", use.Access);
                reference.Origin = "simaticML";
                reference.Location = use.Site;
                reference.CallPins.AddRange(use.Pins);
                return reference;
            }
            var passPins = AuditRules("FB", 82, "Target");
            var failPins = AuditRules("FC", 7, "Peer");
            check(Status(Row(Report(passPins, Point("Pins", "%I0.1", Reference(calls[0]), Reference(calls[1]))), "Pins")) == "PASS"
                && Status(Row(Report(failPins, Point("Pins", "%I0.1", Reference(calls[2]), Reference(calls[3]))), "Pins")) == "FAIL",
                "I audit: apply existing pin rules to STL Input/InOut and Output/Return");
            var wrongCaller = Row(Report(Audit(), Point("InStl", "%I0.1", Reference(uses[0]))), "InStl");
            check(Status(wrongCaller) == "FAIL" && wrongCaller["violations"]!.AsArray().Any(v => v!["code"]!.ToString() == "WRONG_CALLER"),
                "I audit: STL use in FB81 violates the LAD caller requirement");

            var partial = PlcInputSimaticMl.Scan(Document(Stl(
                Instruction("A", """<Access Scope="Address"><Indirect><Access Scope="Address"><Address Area="Input" BitOffset="3" Type="Bool"/></Access></Indirect></Access>""")
                + Instruction("L", """<Access Scope="TypedConstant"><Constant><ConstantType>Pointer</ConstantType><ConstantValue>P#I 0.0</ConstantValue></Constant></Access>""")
                + Instruction("Mystery", address) + "<Unexpected/>"), "<StructuredText/>"), issues, expectStl: true);
            check(issues.Count == 5 && partial.Count == 1 && partial[0].Access == "Unknown" && !partial[0].AccessComplete
                && !partial[0].NetworkScanComplete, "I audit: incomplete STL indirect, pointer, unknown instruction and unsupported XML");
        }

        private static PlcInputUsageAudit AuditRules(string? type, int? number, string? name, bool pins = true,
            bool caller = false, bool graph = false)
            => new PlcInputUsageAudit(new[] { "%I0.0..%I2.7" }, false, null, caller, "FB", 81, "LAD",
                pins, type, number, name, graph);

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

        private static void CheckReferenceSources(Action<bool, string> check)
        {
            var native = Ref("FB", 81, "LAD", "ReadAndSymbol");
            native.BlockName = "Caller";
            native.Network = 23;
            native.CallPins.Add(new PlcInputCallPin { BlockType = "FC", BlockNumber = 7, BlockName = "Other", Pin = "ignored", Direction = "Output" });
            var otherNetwork = Ref("FB", 81, "LAD", "Read");
            otherNetwork.Network = 36;
            var differentSymbol = Ref("FB", 81, "LAD", "Read");
            differentSymbol.Network = 23;
            differentSymbol.Symbol = "Tag.Member";
            PlcInputAuditReference ParsedPin(string site, string pin)
            {
                var use = Ref("FB", 81, "LAD", "Read");
                use.Origin = "simaticML";
                use.Network = 23;
                use.Location = site;
                use.SymbolComponents = new[] { "Tag" };
                use.CallPins.Add(new PlcInputCallPin { BlockType = "FB", BlockNumber = 82, BlockName = "Target", Pin = pin, Direction = "Input" });
                return use;
            }

            var audit = AuditRules("FB", 82, "Target", caller: true);
            var first = ParsedPin("NW23/1", "input1");
            var row = Row(Report(audit, Point("Tag", "%I0.0", native, first)), "Tag");
            var reference = row["references"]!.AsArray().Single()!;
            check(Status(row) == "PASS" && reference["callerOrigin"]!.ToString() == "crossReference"
                && reference["blockName"]!.ToString() == "Caller" && reference["origin"]!.ToString() == "simaticML"
                && row["callPinConnections"]!.AsArray().Single()!["pin"]!.ToString() == "input1",
                "I audit: summarize one symbolic usage with a native caller and an XML pin");

            row = Row(Report(audit, Point("Tag", "%I0.0", native, first, ParsedPin("NW23/2", "input2"), otherNetwork, differentSymbol)), "Tag");
            check(row["references"]!.AsArray().Count == 4 && row["callPinConnections"]!.AsArray().Count == 2,
                "I audit: preserve distinct XML pins, networks and symbols");
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
