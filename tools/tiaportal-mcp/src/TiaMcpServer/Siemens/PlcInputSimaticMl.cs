using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    internal sealed class PlcInputXmlUse
    {
        internal PlcInputSpan Span { get; set; }
        internal bool Absolute { get; set; }
        internal string[]? Symbol { get; set; }
        internal string Access { get; set; } = "Unknown";
        internal bool AccessComplete { get; set; } = true;
        internal bool HasOtherWrite { get; set; }
        internal bool NetworkScanComplete { get; set; } = true;
        internal bool CallPinTraceComplete { get; set; } = true;
        internal int Network { get; set; }
        internal string Site { get; set; } = "";
        internal bool Confirmed { get; set; }
        internal string? GraphRegion { get; set; }
        internal int? GraphOperationNumber { get; set; }
        internal int? GraphSequence { get; set; }
        internal string? TransitionName { get; set; }
        internal int? TransitionNumber { get; set; }
        internal List<PlcInputCallPin> Pins { get; } = new List<PlcInputCallPin>();
    }

    internal static class PlcInputSimaticMl
    {
        internal static List<PlcInputXmlUse> Scan(XDocument xml, IList<string> issues, IList<string>? graphIssues = null,
            bool expectGraph = false, bool expectStl = false, ISet<int>? stlNetworks = null)
        {
            graphIssues ??= issues;
            var operands = new List<PlcInputXmlUse>();
            var units = xml.Descendants().Where(e => Is(e, "SW.Blocks.CompileUnit")).ToList();
            if (units.Count == 0)
                issues.Add("the XML has no compile units");
            bool hasGraph = false;

            for (int i = 0; i < units.Count; i++)
            {
                int network = i + 1;
                var attributes = Child(units[i], "AttributeList");
                var source = Child(attributes, "NetworkSource");
                var stl = (Child(attributes, "ProgrammingLanguage")?.Value ?? (expectStl ? "STL" : null)) == "STL"
                    || source?.Elements().Any(PlcInputStatementList.IsStatementList) == true;
                if (stl) stlNetworks?.Add(network);
                if (source == null)
                {
                    issues.Add($"NW{network}: missing NetworkSource");
                    continue;
                }
                var content = source.Elements().ToList();
                int firstOperand = operands.Count, ordinaryIssueCount = issues.Count, graphIssueCount = graphIssues.Count;
                if (stl)
                {
                    if (content.Count != 1 || !PlcInputStatementList.IsStatementList(content[0]))
                        issues.Add($"NW{network}: unsupported or missing STL NetworkSource");
                    else
                        PlcInputStatementList.Scan(content[0], network, operands, issues);
                }
                else if (content.Count == 0 && !string.IsNullOrWhiteSpace(source.Value))
                    issues.Add($"NW{network}: unstructured NetworkSource");
                foreach (var body in stl ? Enumerable.Empty<XElement>() : content)
                {
                    if (Is(body, "Graph"))
                    {
                        hasGraph = true;
                        ScanGraph(body, network, operands, issues, graphIssues);
                        continue;
                    }
                    if (!Is(body, "FlgNet") && !Is(body, "StructuredText"))
                        issues.Add($"NW{network}: unsupported network format {body.Name.LocalName}");
                    ScanBody(body, network, $"NW{network}", operands, issues,
                        traceContacts: Child(attributes, "ProgrammingLanguage")?.Value != "FBD");
                }
                if (issues.Count != ordinaryIssueCount || graphIssues.Count != graphIssueCount)
                    for (int operand = firstOperand; operand < operands.Count; operand++)
                        operands[operand].NetworkScanComplete = false;
            }
            if (expectGraph && !hasGraph) graphIssues.Add("GRAPH NetworkSource was not available");
            return operands;
        }

        private static void ScanGraph(XElement graph, int network, List<PlcInputXmlUse> operands,
            IList<string> issues, IList<string> graphIssues)
        {
            void ScanRegion(XElement container, string label, string? region, int? sequence, bool requiresNetwork,
                int? operationNumber = null)
            {
                var regionIssues = region == null ? issues : graphIssues;
                var networks = container.Elements().Where(e => Is(e, "FlgNet")).ToList();
                if (networks.Count > 1 || requiresNetwork && networks.Count == 0
                    || container.Elements().Any(e => e.Name.LocalName is not ("FlgNet" or "Title" or "Comment" or "TransitionName" or "AlarmText")))
                    regionIssues.Add($"{label}: unexpected or missing GRAPH network");
                int? transitionNumber = null;
                var transitionName = region == "Transition" ? (string?)container.Attribute("Name") : null;
                if (region == "Transition")
                {
                    if (int.TryParse((string?)container.Attribute("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                        transitionNumber = number;
                    else
                        regionIssues.Add($"{label}: GRAPH transition number could not be read");
                }
                for (int i = 0; i < networks.Count; i++)
                    ScanBody(networks[i], network, $"{label}/NW{i + 1}", operands, regionIssues, region, sequence,
                        transitionName, transitionNumber, operationNumber);
            }

            foreach (var section in graph.Elements().Where(e => e.Name.LocalName is "PreOperations" or "PostOperations"))
            {
                var region = Is(section, "PreOperations") ? "PermanentPreInstructions" : "PermanentPostInstructions";
                if (section.Elements().Any(e => e.Name.LocalName is not ("PermanentOperation" or "Title" or "Comment")))
                    graphIssues.Add($"{region}: unexpected GRAPH instruction container");
                int index = 0;
                foreach (var operation in section.Elements().Where(e => Is(e, "PermanentOperation")))
                {
                    index++;
                    ScanRegion(operation, $"NW{network}/{region}{index}", region, null, false, index);
                }
            }

            var sequences = graph.Elements().Where(e => Is(e, "Sequence")).ToList();
            if (sequences.Count == 0) graphIssues.Add($"NW{network}: GRAPH Sequence is missing");
            for (int i = 0; i < sequences.Count; i++)
            {
                var prefix = $"NW{network}/Sequence{i + 1}";
                var transitions = Child(sequences[i], "Transitions");
                if (transitions == null || transitions.Elements().Any(e => !Is(e, "Transition")))
                    graphIssues.Add($"{prefix}: GRAPH Transitions could not be read");
                int index = 0;
                foreach (var transition in transitions?.Elements().Where(e => Is(e, "Transition")) ?? Enumerable.Empty<XElement>())
                    ScanRegion(transition, $"{prefix}/Transition{++index}", "Transition", i + 1, true);
                foreach (var step in Child(sequences[i], "Steps")?.Elements().Where(e => Is(e, "Step")) ?? Enumerable.Empty<XElement>())
                {
                    var label = $"{prefix}/Step{(string?)step.Attribute("Number")}";
                    int action = 0;
                    foreach (var a in Child(step, "Actions")?.Elements().Where(e => Is(e, "Action")) ?? Enumerable.Empty<XElement>())
                        ScanBody(a, network, $"{label}/Action{++action}", operands, issues);
                    foreach (var region in new[] { "Interlock", "Supervision" })
                    {
                        var section = Child(Child(step, region + "s"), region);
                        if (section != null) ScanRegion(section, $"{label}/{region}", null, i + 1, true);
                    }
                }
            }
        }

        private static void ScanBody(XElement body, int network, string site, List<PlcInputXmlUse> operands,
            IList<string> issues, string? graphRegion = null, int? graphSequence = null,
            string? transitionName = null, int? transitionNumber = null, int? graphOperationNumber = null,
            bool traceContacts = true)
        {
            int operand = 0;
            var graphical = Is(body, "FlgNet");
            var scl = Is(body, "StructuredText");
            var parts = Child(body, "Parts");
            var wires = Child(body, "Wires");
            if (graphical && parts == null) issues.Add($"{site}: FlgNet Parts are missing");
            var partsById = parts?.Elements().Where(p => p.Attribute("UId") != null)
                .ToLookup(p => (string)p.Attribute("UId")!);
            var wiresById = wires?.Elements().SelectMany(w => w.Elements().Where(c => Is(c, "IdentCon") && c.Attribute("UId") != null)
                .Select(c => (Id: (string)c.Attribute("UId")!, Wire: w))).ToLookup(c => c.Id, c => c.Wire);
            var contactPorts = traceContacts ? wires?.Elements().SelectMany(w => w.Elements()
                .Where(c => Is(c, "NameCon") && ((string?)c.Attribute("Name") is "in" or "out") && c.Attribute("UId") != null)
                .Select(c => (Key: ((string)c.Attribute("UId")!, (string)c.Attribute("Name")!), Wire: w)))
                .ToLookup(c => c.Key, c => c.Wire) : null;
            foreach (var node in body.Descendants().Where(e => e.Name.LocalName is "Access" or "Address" or "Token"))
            {
                if (node.Ancestors().TakeWhile(a => a != body).Any(a => a.Name.LocalName is "LineComment" or "BlockComment" or "Comment" or "Title"))
                    continue;
                var access = Is(node, "Access") ? node : node.Ancestors().FirstOrDefault(e => Is(e, "Access"));
                var scope = (string?)access?.Attribute("Scope");
                var area = (string?)node.Attribute("Area");
                var token = Is(node, "Token") ? Token(node) : null;
                var use = new PlcInputXmlUse
                {
                    Network = network, GraphRegion = graphRegion, GraphSequence = graphSequence,
                    TransitionName = transitionName, TransitionNumber = transitionNumber, GraphOperationNumber = graphOperationNumber,
                };

                if (Is(node, "Access") && scope == "GlobalVariable")
                {
                    var symbol = Child(node, "Symbol");
                    if (symbol == null) continue;
                    use.Symbol = Components(symbol);
                    if (use.Symbol.Length == 0) continue;
                    use.Confirmed = true;
                }
                else if (area is "Input" or "PeripheryInput" || LooksLikeAddressToken(token))
                {
                    if ((bool?)node.Attribute("Informative") == true || Is(node.Parent!, "Symbol") && scope == "GlobalVariable")
                        continue;
                    if (!(area == null ? PlcInputSpan.TryParse(WithoutPeriphery(token!), out var span) : TryAddress(node, out span)))
                    {
                        issues.Add($"{site}: input operand has an unsupported address or width");
                        continue;
                    }
                    use.Absolute = true;
                    use.Span = span;
                    use.Confirmed = Is(node, "Address") && access != null && scope is not ("AddressConstant" or "Reference")
                        || Is(node, "Token") && node.Parent == body;
                }
                else continue;

                var uid = (string?)(access ?? node).Attribute("UId");
                use.Site = $"{site}/{++operand}" + (uid == null ? "" : $" (UId {uid})");
                if (graphical)
                {
                    var indexOwner = uid == null ? GraphicalArrayOwner(access) : null;
                    var wiredAccess = indexOwner ?? access;
                    var wireUid = (string?)wiredAccess?.Attribute("UId");
                    if (wiredAccess != null && wireUid != null && partsById != null && wiresById != null
                        && partsById[wireUid].SingleOrDefaultIfUnique() == wiredAccess
                        && (indexOwner == null || wiresById[wireUid].Any(w => w.Elements().Any(p => Is(p, "NameCon")
                            && ConnectedGraphicalPart(partsById, (string?)p.Attribute("UId"), (string?)p.Attribute("Name")) != null))))
                    {
                        // An array index is read to address the wired operand. It has no pin of its own.
                        use.Access = indexOwner == null
                            ? GraphicalAccess(wiresById[wireUid], partsById, contactPorts, use, issues)
                            : "Read";
                    }
                    else
                    {
                        issues.Add($"{use.Site}: the FlgNet operand could not be matched to its wires");
                        use.Confirmed = use.AccessComplete = false;
                    }
                }
                else
                {
                    var operandNode = access ?? node;
                    if (scl) use.Access = StructuredTextAccess(body, operandNode);
                    var parameter = operandNode.Parent;
                    if (parameter != null && parameter.Name.LocalName is "Parameter" or "NamelessParameter"
                        && parameter.Parent is XElement call && Is(call, "CallInfo")
                        && parameter.Elements().All(e => e == operandNode
                            || e.Name.LocalName is "Blank" or "NewLine" or "Comment" or "LineComment" or "BlockComment"
                            || Token(e) is ":=" or "=>"))
                    {
                        var direction = (string?)parameter.Attribute("Section");
                        if (direction == null && parameter.Elements().Any(e => Token(e) == "=>"))
                            direction = "Output";
                        if (use.Access == "Unknown") use.Access = SectionAccess(direction);
                        if (Is(node, "Token") && use.Absolute) use.Confirmed = true;
                        use.Pins.Add(CallPin(call, (string?)parameter.Attribute("Name"), direction, (string?)call.Attribute("UId")));
                    }
                }
                operands.Add(use);
            }
        }

        internal static string[] Components(XElement e) => e.Elements().Where(c => Is(c, "Component"))
            .Select(c => (string?)c.Attribute("Name") ?? "").Where(n => n.Length != 0).ToArray();

        private static bool Is(XElement e, string name) => e.Name.LocalName == name;
        internal static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(child => Is(child, name));
        private static bool LooksLikeAddressToken(string? token) => token != null
            && token.StartsWith("%I", StringComparison.OrdinalIgnoreCase);
        internal static string WithoutPeriphery(string token) => token.EndsWith(":P", StringComparison.OrdinalIgnoreCase)
            ? token.Substring(0, token.Length - 2) : token;

        internal static bool TryAddress(XElement node, out PlcInputSpan span)
        {
            span = default;
            if (!int.TryParse((string?)node.Attribute("BitOffset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                return false;
            long width;
            switch (((string?)node.Attribute("Type"))?.ToUpperInvariant())
            {
                case "BOOL": case "BIT": width = 1; break;
                case "BYTE": case "CHAR": case "USINT": case "SINT": width = 8; break;
                case "WORD": case "INT": case "UINT": case "WCHAR": case "S5TIME": case "DATE": width = 16; break;
                case "DWORD": case "DINT": case "UDINT": case "REAL": case "TIME": case "TIME_OF_DAY": case "TOD": width = 32; break;
                default: return false;
            }
            span = new PlcInputSpan(offset, offset + width - 1);
            return true;
        }

        private static XElement? GraphicalArrayOwner(XElement? access)
        {
            var component = access?.Parent;
            var symbol = component?.Parent;
            var owner = symbol?.Parent;
            return component?.Name.LocalName == "Component" && (string?)component.Attribute("AccessModifier") == "Array"
                && symbol?.Name.LocalName == "Symbol" && owner?.Name.LocalName == "Access" ? owner : null;
        }

        private static XElement? ConnectedGraphicalPart(ILookup<string, XElement> parts, string? uid, string? pin)
        {
            var part = parts[uid ?? ""].SingleOrDefaultIfUnique();
            return !string.IsNullOrEmpty(pin) && part?.Name.LocalName is "Call" or "Part" ? part : null;
        }

        private static string GraphicalAccess(IEnumerable<XElement> wires, ILookup<string, XElement> parts,
            ILookup<(string, string), XElement>? contactPorts, PlcInputXmlUse use, IList<string> issues)
        {
            var directions = new HashSet<string>();
            bool hasEndpoint = false;
            var connectedPins = new HashSet<(string?, string?, PlcInputPinSource)>();
            void AddPin(XElement? call, string? pin, string? section, string? uid, PlcInputPinSource source)
            {
                if (!connectedPins.Add((uid, pin, source))) return;
                var connection = CallPin(call, pin, section, uid);
                connection.Source = source;
                use.Pins.Add(connection);
            }

            foreach (var wire in wires)
            {
                foreach (var port in wire.Elements().Where(c => Is(c, "NameCon")))
                {
                    var uid = (string?)port.Attribute("UId");
                    var pin = (string?)port.Attribute("Name");
                    var part = ConnectedGraphicalPart(parts, uid, pin);
                    if (part == null)
                    {
                        issues.Add($"{use.Site}: could not resolve a unique part or port for UId {uid}");
                        directions.Add("Unknown");
                        use.HasOtherWrite = true;
                        continue;
                    }
                    hasEndpoint = true;
                    if (Is(part, "Call"))
                    {
                        var call = Child(part, "CallInfo");
                        var parameter = CallParameter(call, pin!);
                        var section = parameter == null ? pin == "en" ? "Input" : null : (string?)parameter.Attribute("Section");
                        directions.Add(SectionAccess(section));
                        AddPin(call, pin, section, uid, PlcInputPinSource.Direct);
                    }
                    else
                    {
                        var access = InstructionAccess((string?)part.Attribute("Name"), pin);
                        directions.Add(access);
                        use.HasOtherWrite |= access == "Unknown" || PlcInputUsageAudit.IsWrite(access);
                        if (contactPorts != null && pin == "operand" && ContactSource(part) is { } source
                            && !TraceContactPins(uid!, parts, contactPorts, (call, name, section, callUid) =>
                                AddPin(call, name, section, callUid, source)))
                            use.CallPinTraceComplete = false;
                    }
                }
            }
            use.Confirmed &= hasEndpoint;
            use.AccessComplete = hasEndpoint && !directions.Remove("Unknown");
            if (directions.Count == 0) return "Unknown";
            if (directions.Count > 1) return "RW";
            return directions.Single();
        }

        private static XElement? CallParameter(XElement? call, string pin) => call?.Elements()
            .Where(p => Is(p, "Parameter") && (string?)p.Attribute("Name") == pin).SingleOrDefaultIfUnique();

        private static XElement? SingleOrDefaultIfUnique(this IEnumerable<XElement> elements)
        {
            using var iterator = elements.GetEnumerator();
            if (!iterator.MoveNext()) return null;
            var element = iterator.Current;
            return iterator.MoveNext() ? null : element;
        }

        private static PlcInputPinSource? ContactSource(XElement part) => (string?)part.Attribute("Name") switch
        {
            "NegatedContact" => PlcInputPinSource.NegatedContact,
            "Contact" => part.Elements().Any(e => Is(e, "Negated") && (string?)e.Attribute("Name") == "operand")
                ? PlcInputPinSource.NegatedContact : PlcInputPinSource.Contact,
            _ => null,
        };

        private static bool TraceContactPins(string start, ILookup<string, XElement> parts,
            ILookup<(string, string), XElement> wiresByPort, Action<XElement, string, string?, string> addPin)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>();
            var complete = true;
            pending.Push(start);
            while (pending.Count > 0)
            {
                var contact = pending.Pop();
                if (!visited.Add(contact))
                {
                    complete = false;
                    continue;
                }
                var wires = wiresByPort[(contact, "out")].ToList();
                if (wires.Count == 0) continue;
                if (wires.Count != 1)
                {
                    complete = false;
                    continue;
                }
                // Qualifying inputs must be fed by the contact alone, even on a fan-out wire.
                var targets = wires[0].Elements().Where(c => !Is(c, "OpenCon")
                    && !(Is(c, "NameCon") && (string?)c.Attribute("UId") == contact && (string?)c.Attribute("Name") == "out"))
                    .Select(port =>
                    {
                        var uid = (string?)port.Attribute("UId") ?? "";
                        var pin = (string?)port.Attribute("Name") ?? "";
                        var part = parts[uid].SingleOrDefaultIfUnique();
                        var call = Child(part, "CallInfo");
                        return (Port: port, Uid: uid, Pin: pin, Part: part, Call: call, Parameter: CallParameter(call, pin));
                    }).ToList();
                if (targets.Any(t => !Is(t.Port, "NameCon") || t.Part == null || t.Pin.Length == 0
                    || !Is(t.Part, "Call") && !(Is(t.Part, "Part") && IsFlowInput(t.Part, t.Pin))
                    || Is(t.Part, "Call") && (t.Call == null || t.Parameter == null && t.Pin != "en")))
                {
                    complete = false;
                    continue;
                }
                var hasOtherDriver = targets.Any(t => (string?)t.Parameter?.Attribute("Section") is "Output" or "Return");
                foreach (var (_, uid, pin, part, call, parameter) in targets)
                {
                    if (parameter != null)
                    {
                        var section = (string?)parameter.Attribute("Section");
                        if (!hasOtherDriver || section is "Output" or "Return")
                            addPin(call!, pin, section, uid);
                        else complete = false;
                    }
                    else if (Is(part!, "Part") && ContactSource(part!) != null)
                    {
                        if (!hasOtherDriver && wiresByPort[(uid, "in")].Count() == 1) pending.Push(uid);
                        else complete = false;
                    }
                }
            }
            return complete;
        }

        private static bool IsFlowInput(XElement part, string pin)
        {
            if (ContactSource(part) != null) return pin == "in";
            var name = (string?)part.Attribute("Name");
            return !string.IsNullOrEmpty(name) && (pin is "en" or "pre" or "s" or "r"
                || pin.StartsWith("in", StringComparison.OrdinalIgnoreCase) && pin.Skip(2).All(char.IsDigit)
                || name == "CTU" && pin == "CU" || name == "CTD" && pin == "CD"
                || name == "CTUD" && (pin is "CU" or "CD"));
        }

        internal static PlcInputCallPin CallPin(XElement? call, string? pin, string? section, string? callUid)
        {
            var instance = Child(call, "Instance");
            var scope = (string?)instance?.Attribute("Scope");
            var components = instance == null ? Array.Empty<string>() : Components(instance);
            string? kind = null, declaredType = null;
            if (scope == "LocalVariable" && components.Length == 1)
            {
                var block = call!.Ancestors().FirstOrDefault(e => e.Name.LocalName is "SW.Blocks.FB" or "SW.Blocks.FC" or "SW.Blocks.OB");
                var sections = Child(Child(Child(block, "AttributeList"), "Interface"), "Sections");
                var members = sections?.Elements().Where(e => Is(e, "Section")).SelectMany(s => s.Elements())
                    .Where(m => Is(m, "Member") && string.Equals((string?)m.Attribute("Name"), components[0], StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (members?.Count == 1)
                {
                    kind = (string?)members[0].Parent?.Attribute("Name") switch
                    {
                        "Static" => "MultiInstance",
                        "Input" or "InOut" or "Output" => "ParameterInstance",
                        _ => null,
                    };
                    declaredType = kind == null ? null : (string?)members[0].Attribute("Datatype");
                }
            }
            return new PlcInputCallPin
            {
                BlockType = (string?)call?.Attribute("BlockType"),
                BlockName = (string?)call?.Attribute("Name") ??
                    ((string?)call?.Attribute("BlockType") == "FC" && scope == "GlobalVariable" && components.Length == 1
                        ? components[0] : null),
                CallUid = callUid, Pin = pin, Direction = section,
                InstanceScope = scope, InstanceName = instance == null ? null : string.Join(".", components),
                InstanceKind = kind, InstanceTypeName = declaredType,
            };
        }

        internal static ILookup<string, string> ReadPinDirections(XDocument xml)
        {
            var block = xml.Descendants().FirstOrDefault(e => e.Name.LocalName is "SW.Blocks.FB" or "SW.Blocks.FC");
            return ReadInterfacePinDirections(Child(Child(block, "AttributeList"), "Interface"));
        }

        internal static ILookup<string, string> ReadInterfacePinDirections(XElement? blockInterface)
        {
            var sections = Child(blockInterface, "Sections");
            return (sections?.Elements().Where(e => Is(e, "Section")) ?? Enumerable.Empty<XElement>())
                .SelectMany(s => s.Elements().Where(m => Is(m, "Member") && m.Attribute("Name") != null)
                    .Select(m => (Name: (string)m.Attribute("Name")!, Direction: (string?)s.Attribute("Name"))))
                .Where(p => p.Direction is "Input" or "InOut" or "Output" or "Return")
                .ToLookup(p => p.Name, p => p.Direction!, StringComparer.OrdinalIgnoreCase);
        }

        internal static string SectionAccess(string? section) => section switch
        {
            "Input" => "Read", "Output" or "Return" => "Write", "InOut" => "RW", _ => "Unknown"
        };

        private static string InstructionAccess(string? part, string? pin)
        {
            if (pin == "operand")
            {
                if (part is "Contact" or "NegatedContact" or "Powerrail") return "Read";
                if (part is "Coil" or "NegatedCoil" or "SCoil" or "RCoil") return "Write";
            }
            if (part is "PContact" or "NContact" or "PBox" or "NBox" or "PCoil" or "NCoil")
                return pin == "operand" ? (part.EndsWith("Coil", StringComparison.Ordinal) ? "Write" : "Read")
                    : pin == "bit" ? "RW" : "Unknown";
            if (part is "Move" or "MOVE" or "Add" or "Sub" or "Mul" or "Div"
                or "Eq" or "Ne" or "Lt" or "Le" or "Gt" or "Ge" or "Convert")
            {
                if (pin is "in" or "in1" or "in2") return "Read";
                if (pin is "out" or "out1" or "out2") return "Write";
            }
            return "Unknown";
        }

        private static string StructuredTextAccess(XElement scl, XElement operand)
        {
            var parent = operand.Parent;
            if (parent == null) return "Unknown";
            if (Is(parent, "Parameter"))
            {
                if (parent.Elements().Any(e => Token(e) == "=>")) return "Write";
                return SectionAccess((string?)parent.Attribute("Section"));
            }
            if (parent != scl)
            {
                // A nested expression or an untyped positional argument may be passed by reference.
                return "Unknown";
            }
            var next = operand.ElementsAfterSelf().FirstOrDefault(e =>
                !Is(e, "Blank") && !Is(e, "NewLine") && !Is(e, "LineComment") && !Is(e, "BlockComment"));
            return next == null ? "Unknown" : Token(next) switch
            {
                ":=" => "Write",
                "?=" or "=>" or "." or "[" or "^" => "Unknown",
                _ => "Read",
            };
        }

        private static string? Token(XElement node) => Is(node, "Token") ? (string?)node.Attribute("Text") : null;
    }
}
