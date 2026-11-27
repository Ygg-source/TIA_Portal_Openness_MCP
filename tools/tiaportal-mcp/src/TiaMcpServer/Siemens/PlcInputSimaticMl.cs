using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    internal static class PlcInputSimaticMl
    {
        internal static List<(PlcInputSpan Span, string Access, int Network, string Site, bool Confirmed)> Scan(
            XDocument xml, IList<string> issues)
        {
            var operands = new List<(PlcInputSpan, string, int, string, bool)>();
            var units = xml.Descendants().Where(e => Is(e, "SW.Blocks.CompileUnit")).ToList();
            if (units.Count == 0)
                issues.Add("the XML has no compile units");

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
                    if (!Is(body, "FlgNet") && !Is(body, "StructuredText"))
                        issues.Add($"NW{network}: unsupported network format {body.Name.LocalName}");
                    int operand = 0;
                    foreach (var node in body.Descendants().Where(e => Is(e, "Address") || Is(e, "Token")))
                    {
                        if (node.Ancestors().TakeWhile(a => a != body).Any(a =>
                            Is(a, "LineComment") || Is(a, "BlockComment") || Is(a, "Comment")))
                            continue;
                        var area = (string?)node.Attribute("Area");
                        var token = (string?)node.Attribute("Text");
                        if (area != "Input" && area != "PeripheryInput" && !LooksLikeAddressToken(token))
                            continue;
                        if ((bool?)node.Attribute("Informative") == true)
                            continue;

                        PlcInputSpan span;
                        if (!(area == null ? PlcInputSpan.TryParse(WithoutPeriphery(token!), out span) : TryAddress(node, out span)))
                        {
                            issues.Add($"NW{network}: input operand has an unsupported address or width");
                            continue;
                        }

                        var access = node.Ancestors().FirstOrDefault(e => Is(e, "Access"));
                        var graphical = node.Ancestors().FirstOrDefault(e => Is(e, "FlgNet"));
                        var scl = node.Ancestors().FirstOrDefault(e => Is(e, "StructuredText"));
                        var direction = graphical != null && access != null ? GraphicalAccess(graphical, access)
                            : scl != null ? StructuredTextAccess(scl, access ?? node)
                            : "Unknown";
                        var confirmed = Is(node, "Address") && access != null &&
                            (string?)access.Attribute("Scope") is not ("AddressConstant" or "Reference");
                        confirmed |= Is(node, "Token") && node.Parent == scl;
                        var uid = (string?)(access ?? node).Attribute("UId");
                        operands.Add((span, direction, network, $"NW{network}/{++operand}" +
                            (uid == null ? "" : $" (UId {uid})"), confirmed));
                    }
                }
            }
            return operands;
        }

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

        private static string GraphicalAccess(XElement body, XElement access)
        {
            var id = (string?)access.Attribute("UId");
            if (id == null) return "Unknown";
            var parts = Child(body, "Parts");
            var wires = Child(body, "Wires");
            if (parts == null || wires == null) return "Unknown";
            var directions = new HashSet<string>();
            foreach (var wire in wires.Elements().Where(w => w.Elements().Any(c =>
                         Is(c, "IdentCon") && (string?)c.Attribute("UId") == id)))
            {
                foreach (var port in wire.Elements().Where(c => Is(c, "NameCon")))
                {
                    var part = parts.Elements().FirstOrDefault(e => (string?)e.Attribute("UId") == (string?)port.Attribute("UId"));
                    var pin = (string?)port.Attribute("Name");
                    var call = Child(part, "CallInfo");
                    var parameter = call?.Elements().FirstOrDefault(p => Is(p, "Parameter") && (string?)p.Attribute("Name") == pin);
                    directions.Add(parameter != null ? SectionAccess((string?)parameter.Attribute("Section"))
                        : InstructionAccess((string?)part?.Attribute("Name"), pin));
                }
            }
            if (directions.Count == 0 || directions.Contains("Unknown")) return "Unknown";
            if (directions.Count > 1) return "RW";
            return directions.Single();
        }

        private static string SectionAccess(string? section) => section switch
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
