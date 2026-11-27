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
        internal int Network { get; set; }
        internal string Site { get; set; } = "";
        internal bool Confirmed { get; set; }
        internal string? GraphRegion { get; set; }
        internal int? GraphSequence { get; set; }
        internal string? TransitionName { get; set; }
        internal int? TransitionNumber { get; set; }
        internal List<PlcInputCallPin> Pins { get; } = new List<PlcInputCallPin>();
    }

    internal static class PlcInputSimaticMl
    {
        internal static List<PlcInputXmlUse> Scan(XDocument xml, IList<string> issues, IList<string>? graphIssues = null,
            bool expectGraph = false)
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
                var source = Child(Child(units[i], "AttributeList"), "NetworkSource");
                if (source == null)
                {
                    issues.Add($"NW{network}: missing NetworkSource");
                    continue;
                }
                var content = source.Elements().ToList();
                if (content.Count == 0 && !string.IsNullOrWhiteSpace(source.Value))
                    issues.Add($"NW{network}: unstructured NetworkSource");
                foreach (var body in content)
                {
                    if (Is(body, "Graph"))
                    {
                        hasGraph = true;
                        ScanGraph(body, network, operands, issues, graphIssues);
                        continue;
                    }
                    if (!Is(body, "FlgNet") && !Is(body, "StructuredText"))
                        issues.Add($"NW{network}: unsupported network format {body.Name.LocalName}");
                    ScanBody(body, network, $"NW{network}", operands, issues);
                }
            }
            if (expectGraph && !hasGraph) graphIssues.Add("GRAPH NetworkSource was not available");
            return operands;
        }

        private static void ScanGraph(XElement graph, int network, List<PlcInputXmlUse> operands,
            IList<string> issues, IList<string> graphIssues)
        {
            void ScanRegion(XElement container, string label, string? region, int? sequence, bool requiresNetwork)
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
                        transitionName, transitionNumber);
            }

            foreach (var section in graph.Elements().Where(e => e.Name.LocalName is "PreOperations" or "PostOperations"))
            {
                var region = Is(section, "PreOperations") ? "PermanentPreInstructions" : "PermanentPostInstructions";
                if (section.Elements().Any(e => e.Name.LocalName is not ("PermanentOperation" or "Title" or "Comment")))
                    graphIssues.Add($"{region}: unexpected GRAPH instruction container");
                int index = 0;
                foreach (var operation in section.Elements().Where(e => Is(e, "PermanentOperation")))
                    ScanRegion(operation, $"NW{network}/{region}{++index}", region, null, false);
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
            string? transitionName = null, int? transitionNumber = null)
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
                    TransitionName = transitionName, TransitionNumber = transitionNumber,
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
                if (graphical && uid != null && partsById != null && wiresById != null)
                    use.Access = GraphicalAccess(wiresById[uid], partsById, use, issues);
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

        private static string[] Components(XElement e) => e.Elements().Where(c => Is(c, "Component"))
            .Select(c => (string?)c.Attribute("Name") ?? "").Where(n => n.Length != 0).ToArray();

        private static bool Is(XElement e, string name) => e.Name.LocalName == name;
        private static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(child => Is(child, name));
        private static bool LooksLikeAddressToken(string? token) => token != null
            && token.StartsWith("%I", StringComparison.OrdinalIgnoreCase);
        private static string WithoutPeriphery(string token) => token.EndsWith(":P", StringComparison.OrdinalIgnoreCase)
            ? token.Substring(0, token.Length - 2) : token;

        private static bool TryAddress(XElement node, out PlcInputSpan span)
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

        private static string GraphicalAccess(IEnumerable<XElement> wires, ILookup<string, XElement> parts,
            PlcInputXmlUse use, IList<string> issues)
        {
            var directions = new HashSet<string>();
            var connectedPins = new HashSet<(string?, string?)>();
            foreach (var wire in wires)
            {
                foreach (var port in wire.Elements().Where(c => Is(c, "NameCon")))
                {
                    var callUid = (string?)port.Attribute("UId");
                    var matchingParts = parts[callUid ?? ""].ToList();
                    var pin = (string?)port.Attribute("Name");
                    if (matchingParts.Count != 1)
                    {
                        issues.Add($"{use.Site}: could not resolve a unique part UId {callUid}");
                        directions.Add("Unknown");
                        use.HasOtherWrite = true;
                        continue;
                    }
                    var part = matchingParts[0];
                    if (Is(part, "Call"))
                    {
                        var call = Child(part, "CallInfo");
                        var parameters = call?.Elements().Where(p => Is(p, "Parameter") && (string?)p.Attribute("Name") == pin).ToList();
                        var section = parameters?.Count == 1 ? (string?)parameters[0].Attribute("Section") : pin == "en" ? "Input" : null;
                        directions.Add(SectionAccess(section));
                        if (connectedPins.Add((callUid, pin)))
                            use.Pins.Add(CallPin(call, pin, section, callUid));
                    }
                    else
                    {
                        var access = InstructionAccess((string?)part.Attribute("Name"), pin);
                        directions.Add(access);
                        use.HasOtherWrite |= access == "Unknown" || PlcInputUsageAudit.IsWrite(access);
                    }
                }
            }
            use.AccessComplete = !directions.Remove("Unknown");
            if (directions.Count == 0) return "Unknown";
            if (directions.Count > 1) return "RW";
            return directions.Single();
        }

        private static PlcInputCallPin CallPin(XElement? call, string? pin, string? section, string? callUid)
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
            var sections = Child(Child(Child(block, "AttributeList"), "Interface"), "Sections");
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
