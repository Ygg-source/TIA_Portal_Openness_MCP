using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using static TiaMcpServer.Siemens.PlcInputSimaticMl;

namespace TiaMcpServer.Siemens
{
    internal static class PlcInputStatementList
    {
        private const string NamespacePrefix = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StatementList/";

        internal static bool IsStatementList(XElement node) => node.Name.LocalName == "StatementList"
            && node.Name.NamespaceName is NamespacePrefix + "v5" or NamespacePrefix + "v4";

        internal static void Scan(XElement body, int network, List<PlcInputXmlUse> uses, IList<string> issues)
        {
            int index = 0;
            if (HasText(body)) issues.Add($"NW{network}: unstructured STL text");
            foreach (var statement in body.Elements())
            {
                var site = $"NW{network}/STL{++index}";
                if (statement.Name != body.Name.Namespace + "StlStatement" || HasText(statement))
                {
                    issues.Add($"{site}: unsupported STL statement structure");
                    continue;
                }
                var parts = statement.Elements().Where(e => !IsTrivia(e) && e.Name.LocalName != "LabelDeclaration").ToList();
                if (parts.Count == 0) continue;
                var token = parts[0];
                var mnemonic = token.Name.LocalName == "StlToken" ? (string?)token.Attribute("Text") : null;
                var arguments = (mnemonic == null ? parts : parts.Skip(1)).ToList();
                if (mnemonic != null && Child(token, "Token") is { } operandToken
                    && (mnemonic is not ("NOP_0" or "NOP_1") || (string?)operandToken.Attribute("Text") is not ("0" or "1")))
                    arguments.Insert(0, operandToken);

                switch (mnemonic)
                {
                    case "CALL": case "CC": case "UC": case "MOVE": case "MOVE_BLOCK":
                        if (arguments.Count == 1 && (Child(arguments[0], "CallInfo") ?? Child(arguments[0], "Instruction")) is { } call)
                        {
                            ScanCall(call, network, site, uses, issues);
                            continue;
                        }
                        break;
                    // SET changes RLO and SAVE copies RLO to BR. Neither takes an operand.
                    case "SET": case "SAVE": case "CLR": case "NEG": case "NOT":
                    case "A_BRACK": case "AN_BRACK": case "O_BRACK": case "ON_BRACK":
                    case "X_BRACK": case "XN_BRACK": case "BRACKET":
                    case "BEC": case "BE": case "BEU": case "NOP_0": case "NOP_1":
                    case "COMMENT": case "EMPTY_LINE": case "CAC": case "PUSH": case "POP": case "ENT": case "LEAVE":
                    case "ADD_I": case "ADD_D": case "ADD_R": case "SUB_I": case "SUB_D": case "SUB_R":
                    case "MUL_I": case "MUL_D": case "MUL_R": case "DIV_I": case "DIV_D": case "DIV_R": case "MOD_D":
                    case "LT_I": case "LT_D": case "LT_R": case "LE_I": case "LE_D": case "LE_R":
                    case "EQ_I": case "EQ_D": case "EQ_R": case "NE_I": case "NE_D": case "NE_R":
                    case "GE_I": case "GE_D": case "GE_R": case "GT_I": case "GT_D": case "GT_R":
                    case "ITD": case "DTR": case "BTI": case "ITB": case "BTD": case "DTB": case "RND":
                    case "RND_M": case "RND_P": case "TRUNC": case "CAW": case "CAD":
                    case "LAR1_ACCU1": case "LAR2_ACCU1": case "LAR1_AR2": case "TAR1_ACCU1": case "TAR2_ACCU1":
                    case "TAR1_AR2": case "CAR": case "CDB": case "L_DBLG": case "L_DILG": case "L_DBNO": case "L_DINO":
                        if (arguments.Count != 0) issues.Add($"{site}: unexpected operand for {mnemonic}");
                        continue;
                    case "JU": case "JC": case "JCN": case "JCB": case "JNB": case "JBI": case "JNBI":
                    case "JO": case "JOS": case "JZ": case "JN": case "JUN": case "JP": case "JM":
                    case "JPZ": case "JMZ": case "JL": case "LOOP":
                        if (arguments.Count != 1 || Child(arguments[0], "Label") == null)
                            issues.Add($"{site}: unrecognized jump destination for {mnemonic}");
                        continue;
                }

                var access = mnemonic switch
                {
                    "A" or "AN" or "O" or "ON" or "X" or "XN" or "L" or "LC" or "LAR1" or "LAR2"
                        or "OPEN_DB" or "OPEN_DI" or "AW" or "OW" or "XOW" or "AD" or "OD" or "XOD" => "Read",
                    "Assign" or "=" or "S" or "R" or "T" or "TAR1" or "TAR2" or "Free"
                        or "OffDelay" or "OnDelay" or "Retentive" or "Pulse" or "Extend" or "CU" or "CD" => "Write",
                    "Rise" or "Fall" or "FP" or "FN" => "RW",
                    _ => "Unknown",
                };
                if (arguments.Count == 0 && mnemonic is "O" or "AW" or "OW" or "XOW" or "AD" or "OD" or "XOD")
                    continue;
                if (access == "Unknown" || arguments.Count != 1)
                {
                    issues.Add($"{site}: unsupported STL instruction or operand count ({mnemonic ?? "missing StlToken"})");
                    access = "Unknown";
                }
                for (int i = 0; i < arguments.Count; i++)
                    if (ReadOperand(arguments[i], access, network, $"{site}/{i + 1}", issues) is { } use)
                        uses.Add(use);
            }
        }

        private static void ScanCall(XElement call, int network, string site, List<PlcInputXmlUse> uses, IList<string> issues)
        {
            var isBlock = call.Name.LocalName == "CallInfo";
            if (HasText(call)) issues.Add($"{site}: unstructured STL call");
            var uid = (string?)call.Attribute("UId") ?? site;
            var number = call.Elements().FirstOrDefault(e => e.Name.LocalName == "IntegerAttribute"
                && (string?)e.Attribute("Name") == "BlockNumber")?.Value;
            var instanceAddress = Child(Child(call, "Instance"), "Address");
            if (number == null && (string?)instanceAddress?.Attribute("Area") is "FB" or "FC"
                && (string?)instanceAddress?.Attribute("Area") == (string?)call.Attribute("BlockType"))
                number = (string?)instanceAddress?.Attribute("BlockNumber") ?? (string?)instanceAddress?.Attribute("BitOffset");
            int? blockNumber = int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0 ? parsed : null;
            int index = 0;
            foreach (var parameter in call.Elements())
            {
                var name = parameter.Name.LocalName;
                if (IsTrivia(parameter) || IsMetadata(parameter) || name is "Instance" or "TemplateValue"
                    || name == "Token" && (string?)parameter.Attribute("Text") is "(" or ")" or ",")
                    continue;
                var parameterSite = $"{site}/{++index}";
                if (name is not ("Parameter" or "NamelessParameter"))
                {
                    issues.Add($"{parameterSite}: unsupported STL call structure");
                    continue;
                }
                if (IsInformative(parameter)) continue;
                var arguments = parameter.Elements().Where(e => !IsTrivia(e) && !IsMetadata(e)
                    && !(e.Name.LocalName == "Token" && (string?)e.Attribute("Text") is ":=" or "=>")).ToList();
                if (arguments.Count == 0 && !HasText(parameter)) continue;
                var direct = arguments.Count == 1 && !HasText(parameter);
                if (!direct) issues.Add($"{parameterSite}: unsupported STL parameter operand structure");
                var direction = direct ? (string?)parameter.Attribute("Section") : null;
                if (direct && direction == null && parameter.Elements().Any(e => e.Name.LocalName == "Token"
                    && (string?)e.Attribute("Text") == "=>")) direction = "Output";
                var access = SectionAccess(direction);
                if (access == "Unknown") direction = null;
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (ReadOperand(arguments[i], access, network, $"{parameterSite}/{i + 1}", issues) is not { } use)
                        continue;
                    if (isBlock && direct)
                    {
                        var pin = CallPin(call, (string?)parameter.Attribute("Name"), direction, uid);
                        pin.BlockNumber = blockNumber;
                        use.Pins.Add(pin);
                    }
                    else if (access == "Unknown")
                        issues.Add($"{parameterSite}: STL parameter direction could not be confirmed");
                    uses.Add(use);
                }
            }
        }

        private static PlcInputXmlUse? ReadOperand(XElement operand, string access, int network, string site, IList<string> issues)
        {
            PlcInputXmlUse Direct() => new PlcInputXmlUse
            {
                Network = network, Site = site, Access = access, AccessComplete = access != "Unknown", Confirmed = true,
            };

            if (operand.Name.LocalName == "Token")
            {
                var token = (string?)operand.Attribute("Text");
                if (token != null && token.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                    && PlcInputSpan.TryParse(WithoutPeriphery(token), out var tokenSpan))
                {
                    var use = Direct();
                    use.Absolute = true;
                    use.Span = tokenSpan;
                    return use;
                }
            }
            var scope = (string?)operand.Attribute("Scope");
            var parts = operand.Elements().Where(e => !IsTrivia(e) && !IsMetadata(e)).ToList();
            var value = parts.Count == 1 ? parts[0] : null;
            if (operand.Name.LocalName != "Access" || value == null || HasText(operand))
            {
                issues.Add($"{site}: unsupported STL operand");
                return null;
            }
            var kind = value.Name.LocalName;
            if (scope is "AddressConstant" or "Reference" or "Unnamed" || kind is "Indirect" or "Reference"
                || kind == "Constant" && (Child(value, "ConstantType")?.Value is "Pointer" or "Any"
                    || Child(value, "ConstantValue")?.Value.TrimStart().StartsWith("P#", StringComparison.OrdinalIgnoreCase) == true)
                || kind == "Symbol" && value.Descendants().Any(e => e.Name.LocalName == "Access"
                    && (string?)e.Attribute("Scope") is not ("LiteralConstant" or "TypedConstant")))
            {
                issues.Add($"{site}: indirect, indexed or pointer STL operand");
                return null;
            }
            if (kind == "Symbol" && scope is "GlobalVariable" or "LocalVariable")
            {
                var components = Components(value);
                if (components.Length != 0)
                {
                    if (scope == "LocalVariable") return null;
                    var use = Direct();
                    use.Symbol = components;
                    return use;
                }
            }
            if (kind == "Address" && scope == "Address")
            {
                var area = (string?)value.Attribute("Area");
                if (IsInformative(value) || area != null && area is not ("Input" or "PeripheryInput")) return null;
                if (area != null && TryAddress(value, out var span))
                {
                    var use = Direct();
                    use.Absolute = true;
                    use.Span = span;
                    return use;
                }
            }
            if (kind is "Constant" or "Statusword" or "Label" or "DataType") return null;
            issues.Add($"{site}: unrecognized STL operand ({scope}/{kind})");
            return null;
        }

        private static bool IsTrivia(XElement node) => node.Name.LocalName is "LineComment" or "BlockComment" or "Comment" or "Blank" or "NewLine";
        private static bool IsMetadata(XElement node) => node.Name.LocalName is "IntegerAttribute" or "StringAttribute" or "BooleanAttribute" or "DateAttribute";
        private static bool IsInformative(XElement node) => (string?)node.Attribute("Informative") is "true" or "1";
        private static bool HasText(XElement node) => node.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
    }
}
