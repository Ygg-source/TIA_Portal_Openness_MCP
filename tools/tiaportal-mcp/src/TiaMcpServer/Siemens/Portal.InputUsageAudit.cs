using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static readonly Regex InputReferenceNetwork = new Regex(@"\bNW([0-9]+)\b", RegexOptions.CultureInvariant);

        private sealed class InputAuditBlock
        {
            internal string Path = "";
            internal string Name = "";
            internal string Namespace = "";
            internal string Type = "";
            internal string Language = "";
            internal int Number;
            internal string? Unit;
            internal string? InstanceOf;
            internal ILookup<string, string>? PinDirections;
            internal PlcInputProgramKind Kind;
        }

        internal JsonObject AuditPlcInputUsage(string softwarePath, PlcInputUsageAudit audit)
        {
            const string tool = "AuditPlcInputUsage";
            var plc = RequirePlc(tool, softwarePath);
            var cpu = GetSoftwareContainer(softwarePath)?.Parent as DeviceItem;
            var warnings = new List<string>();
            var inventoryComplete = true;
            var points = new List<PlcInputAuditPoint>();
            var tables = new List<(PlcTagTable Table, string Path)>();
            var blocks = new Dictionary<PlcBlock, InputAuditBlock>();
            var blockPaths = new List<(string Path, PlcBlock Block)>();
            var units = SelectSoftwareUnits(plc, "", tool, out var unitFailure);
            if (unitFailure != null)
            {
                inventoryComplete = false;
                warnings.Add("Software unit enumeration failed: " + unitFailure);
            }

            void CollectBlock(PlcBlock block, string group, string actualGroup, string? unit, bool safetyUnit)
            {
                if (blocks.ContainsKey(block)) return;
                var metadata = new InputAuditBlock { Path = group, Unit = unit };
                blocks.Add(block, metadata);
                try
                {
                    var language = safetyUnit ? null : block.ProgrammingLanguage.ToString();
                    metadata.Kind = PlcInputUsageAudit.ClassifyProgram(language, safetyUnit);
                    metadata.Name = block.Name;
                    metadata.Path += "/" + metadata.Name;
                    blockPaths.Add((metadata.Path, block));
                    if (!string.Equals(group, actualGroup, StringComparison.OrdinalIgnoreCase))
                        blockPaths.Add(($"{actualGroup}/{metadata.Name}", block));
                    metadata.Type = block.GetType().Name;
                    if (metadata.Kind == PlcInputProgramKind.Safety) return;
                    metadata.Language = language!;
                    metadata.Number = block.Number;
                    metadata.Namespace = block.Namespace ?? "";
                    if (block is InstanceDB instance) metadata.InstanceOf = instance.InstanceOfName;
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    if (metadata.Kind == PlcInputProgramKind.Safety) return;
                    metadata.Kind = PlcInputProgramKind.Unknown;
                    warnings.Add($"{metadata.Path}: block identity could not be read: {ex.Message}");
                }
                if (metadata.Kind == PlcInputProgramKind.Unknown)
                {
                    inventoryComplete = false;
                    warnings.Add($"{metadata.Path}: standard/safety program identity could not be confirmed.");
                }
            }

            void CollectSystem(PlcSystemBlockGroup group, string path, string nativePath, string? unit, bool safetyUnit)
            {
                foreach (var block in group.Blocks) CollectBlock(block, path, nativePath, unit, safetyUnit);
                foreach (var child in group.Groups)
                    CollectSystem(child, $"{path}/{child.Name}", $"{nativePath}/{child.Name}", unit, safetyUnit);
            }

            void CollectBlocks(PlcBlockSystemGroup blockGroup, string path, string? unit, bool safetyUnit)
            {
                foreach (var (group, block) in EnumerateUnitBlocks(blockGroup, path))
                    CollectBlock(block, group, blockGroup.Name + group.Substring(path.Length), unit, safetyUnit);
                foreach (var group in blockGroup.SystemBlockGroups)
                    CollectSystem(group, $"{path}/{group.Name}", $"{blockGroup.Name}/{group.Name}", unit, safetyUnit);
            }

            void CollectScope(string prefix, PlcTagTableGroup? tagGroup, PlcBlockSystemGroup? blockGroup, string? unit = null)
            {
                try
                {
                    CollectTagTables(tagGroup ?? throw new InvalidOperationException("missing tag-table group"), prefix, tables);
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    inventoryComplete = false;
                    warnings.Add($"{prefix}: tag-table enumeration failed: {ex.Message}");
                }
                try
                {
                    CollectBlocks(blockGroup ?? throw new InvalidOperationException("missing block group"),
                        prefix.Length == 0 ? "Program blocks" : prefix, unit, false);
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    inventoryComplete = false;
                    warnings.Add($"{prefix}: block enumeration failed: {ex.Message}");
                }
            }

            CollectScope("", plc.TagTableGroup, plc.BlockGroup);
            foreach (var unit in units)
                CollectScope($"{SoftwareUnitsRoot}/{unit.Name}", unit.TagTableGroup, unit.BlockGroup, unit.Name);
            try
            {
                var provider = plc.GetService<PlcUnitProvider>();
                if (provider != null)
                    foreach (var unit in provider.UnitGroup.SafetyUnits)
                        CollectBlocks(unit.BlockGroup, $"{SoftwareUnitsRoot}/{unit.Name}", unit.Name, true);
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                inventoryComplete = false;
                warnings.Add("Safety unit inventory could not be read: " + ex.Message);
            }
            if (!ReadInputAuditSafetyRuntime(cpu, blocks.Values, warnings))
                inventoryComplete = false;
            var byPath = blockPaths.ToLookup(b => b.Path, b => b.Block, StringComparer.OrdinalIgnoreCase);
            var tagIdentities = new Dictionary<string, List<PlcInputAuditPoint?>>(StringComparer.OrdinalIgnoreCase);

            int totalTags = 0, inputTables = 0, nativeQueries = 0, nativeFailures = 0;
            foreach (var (table, tablePath) in tables)
            {
                bool hasInput = false;
                try
                {
                    foreach (var tag in table.Tags)
                    {
                        totalTags++;
                        var name = tag.Name;
                        if (!tagIdentities.TryGetValue(name, out var identities))
                            tagIdentities.Add(name, identities = new List<PlcInputAuditPoint?>());
                        var identity = identities.Count;
                        identities.Add(null);
                        var address = tag.LogicalAddress;
                        if (address == null || !address.TrimStart().StartsWith("%I", StringComparison.OrdinalIgnoreCase)) continue;
                        hasInput = true;
                        if (!PlcInputSpan.TryParse(address, out var span))
                        {
                            inventoryComplete = false;
                            warnings.Add($"{tablePath}/{tag.Name}: unsupported input address '{address}'.");
                            continue;
                        }
                        var possibleStructuredTag = span.Start == span.End
                            && !string.Equals(tag.DataTypeName, "Bool", StringComparison.OrdinalIgnoreCase);
                        if (!audit.InRange(span) && !(possibleStructuredTag && audit.MayContainStart(span.Start))) continue;
                        var point = new PlcInputAuditPoint
                        {
                            Name = name, Address = address, Span = span, Table = tablePath,
                            SoftwareUnit = tablePath.StartsWith(SoftwareUnitsRoot + "/", StringComparison.Ordinal)
                                ? tablePath.Split('/')[1] : null,
                        };
                        nativeQueries++;
                        try
                        {
                            ReadNativeInputReferences(tag, point, plc.Name, blocks, byPath);
                        }
                        catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                        {
                            nativeFailures++;
                            point.ReferencesComplete = false;
                            warnings.Add($"{tablePath}/{point.Name}: {ex.Message}");
                            if (possibleStructuredTag && !audit.InRange(span)) inventoryComplete = false;
                        }

                        if (!audit.InRange(point.Span)) continue;
                        try
                        {
                            point.Comment = ReadComment(tag.Comment);
                        }
                        catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                        {
                            point.Comment["_error"] = ex.Message;
                        }
                        if (point.Comment.ContainsKey("_error"))
                            warnings.Add($"{tablePath}/{point.Name}: comments could not be read completely.");
                        points.Add(point);
                        identities[identity] = point;
                    }
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    inventoryComplete = false;
                    warnings.Add($"{tablePath}: tag enumeration failed: {ex.Message}");
                }
                if (hasInput) inputTables++;
            }

            int xmlScanned = 0, xmlFailures = 0, xmlUnsupported = 0, absoluteOccurrences = 0, graphScanned = 0, graphFailures = 0;
            int xmlExportAttempts = 0, safetyXmlExportAttempts = 0;
            var codeTargets = blocks.Values.Where(b => b.Kind == PlcInputProgramKind.Standard && b.Type is "FB" or "FC")
                .ToLookup(b => b.Name, StringComparer.OrdinalIgnoreCase);
            var qualifiedTargets = codeTargets.SelectMany(g => g).Where(b => b.Namespace.Length != 0)
                .ToLookup(b => b.Namespace + "." + b.Name, StringComparer.OrdinalIgnoreCase);
            var instanceTargets = blocks.Values.Where(b => b.Kind == PlcInputProgramKind.Standard && b.Type == "InstanceDB")
                .ToLookup(b => b.Name, StringComparer.OrdinalIgnoreCase);
            var nativeUses = points.SelectMany(point => point.References.Select(r => (Point: point, Reference: r)))
                .Where(x => x.Reference.ConfirmedUsage && !x.Reference.Absolute)
                .ToLookup(x => (x.Reference.BlockPath, x.Point.Name!.ToUpperInvariant()));
            var untagged = new Dictionary<PlcInputSpan, PlcInputAuditPoint>();
            var callReferences = new List<(PlcInputAuditReference Reference, bool ResolveAccess)>();
            var temp = Path.Combine(Path.GetTempPath(), "tia-input-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var file = new FileInfo(Path.Combine(temp, "block.xml"));
            try
            {
                foreach (var (block, metadata) in blocks.Where(p => PlcInputUsageAudit.CanExport(p.Value.Kind, p.Key is CodeBlock))
                    .Select(p => (p.Key, p.Value)))
                {
                    var path = metadata.Path;
                    var graph = metadata.Language == "GRAPH";
                    try
                    {
                        if (block.IsKnowHowProtected)
                            throw new InvalidOperationException("the block body is know-how protected");
                        xmlExportAttempts++;
                        if (metadata.Kind == PlcInputProgramKind.Safety) safetyXmlExportAttempts++;
                        block.Export(file, ExportOptions.None);
                        var issues = new List<string>();
                        var graphIssues = new List<string>();
                        var xml = XDocument.Load(file.FullName);
                        var matches = PlcInputSimaticMl.Scan(xml, issues, graphIssues, graph);
                        if (metadata.Type is "FB" or "FC") metadata.PinDirections = PlcInputSimaticMl.ReadPinDirections(xml);
                        xmlScanned++;
                        if (graph) graphScanned++;
                        if (graphIssues.Count > 0)
                        {
                            graphFailures++;
                            issues.AddRange(graphIssues);
                        }
                        if (issues.Count > 0)
                        {
                            xmlUnsupported++;
                            warnings.Add($"{path}: {string.Join("; ", issues.Distinct())}.");
                        }
                        var siteCounts = matches.Where(m => m.Symbol != null)
                            .ToLookup(m => (m.Network, RootSymbol(m.Symbol!)));
                        foreach (var match in matches)
                        {
                            List<PlcInputAuditPoint> owners;
                            var access = match.Access;
                            if (match.Absolute)
                            {
                                owners = points.Where(p => p.Span.Overlaps(match.Span)).ToList();
                                if (audit.InRange(match.Span) && !owners.Any(p => p.Span.Contains(match.Span)))
                                {
                                    if (!untagged.TryGetValue(match.Span, out var standalone))
                                    {
                                        standalone = new PlcInputAuditPoint { Address = match.Span.Canonical, Span = match.Span };
                                        untagged.Add(match.Span, standalone);
                                    }
                                    owners.Add(standalone);
                                }
                                if (owners.Count != 0) absoluteOccurrences++;
                            }
                            else
                            {
                                var symbol = match.Symbol!;
                                var name = RootSymbol(symbol);
                                if (!tagIdentities.TryGetValue(name, out var identities)) continue;
                                var candidates = identities.OfType<PlcInputAuditPoint>().ToList();
                                if (candidates.Count == 0) continue;
                                var native = nativeUses[(path, name.ToUpperInvariant())]
                                    .Where(r => (r.Reference.Network == match.Network || r.Reference.Network == null)
                                        && (NativeSymbolMatches(r.Reference.Symbol, symbol)
                                            || (symbol.Length == 1 && r.Reference.Network == match.Network && string.IsNullOrEmpty(r.Reference.Symbol))))
                                    .ToList();
                                owners = native.Select(n => n.Point).Distinct().ToList();
                                if (owners.Count == 0 && identities.Count == 1 && candidates[0].SoftwareUnit == metadata.Unit
                                    && (symbol.Length == 1 || candidates[0].Span.Start != candidates[0].Span.End))
                                    owners.Add(candidates[0]);
                                if (owners.Count != 1)
                                {
                                    foreach (var candidate in candidates)
                                    {
                                        candidate.SymbolScanComplete = false;
                                        if (match.GraphRegion != null) candidate.GraphSymbolScanComplete = false;
                                    }
                                    continue;
                                }
                                if (access == "Unknown" && native.Count == 1 && siteCounts[(match.Network, name)].Count() == 1)
                                    access = native[0].Reference.Access ?? access;
                            }
                            if (owners.Count == 0) continue;
                            var reference = new PlcInputAuditReference
                            {
                                BlockPath = path, BlockType = metadata.Type, BlockNumber = metadata.Number, BlockName = metadata.Name,
                                Language = metadata.Language, Access = access, Location = match.Site, Network = match.Network,
                                AccessComplete = match.AccessComplete || match.Access == "Unknown" && access != "Unknown",
                                Address = match.Absolute ? match.Span.Canonical : null,
                                Symbol = match.Symbol == null ? null : string.Join(".", match.Symbol),
                                Origin = "simaticML", Absolute = match.Absolute, ConfirmedUsage = match.Confirmed,
                                GraphRegion = match.GraphRegion, GraphSequence = match.GraphSequence,
                                TransitionName = match.TransitionName, TransitionNumber = match.TransitionNumber,
                            };
                            if (match.Pins.Count != 0)
                            {
                                reference.CallPins.AddRange(match.Pins);
                                callReferences.Add((reference, match.Access == "Unknown"));
                            }
                            foreach (var owner in owners) owner.References.Add(reference);
                        }
                    }
                    catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                    {
                        xmlFailures++;
                        if (graph) graphFailures++;
                        warnings.Add($"{path}: block XML could not be scanned: {ex.Message}");
                    }
                    finally
                    {
                        if (File.Exists(file.FullName)) File.Delete(file.FullName);
                    }
                }
            }
            finally
            {
                Directory.Delete(temp);
            }
            foreach (var (reference, resolveAccess) in callReferences)
            {
                foreach (var pin in reference.CallPins)
                    ResolveInputCallPin(pin, codeTargets, qualifiedTargets, instanceTargets);
                if (resolveAccess && reference.CallPins.Count == 1 && reference.CallPins[0].Confirmed)
                    reference.Access = PlcInputSimaticMl.SectionAccess(reference.CallPins[0].Direction);
            }

            var cpuModel = ReadInputAuditCpuAttribute(cpu, "TypeName", warnings);
            var orderNumber = ReadInputAuditCpuAttribute(cpu, "OrderNumber", warnings);
            var report = audit.Summarize(points.Concat(untagged.Values), warnings, inventoryComplete,
                xmlFailures == 0 && xmlUnsupported == 0, graphScanComplete: graphFailures == 0);
            report["projectName"] = _project?.Name;
            report["plcName"] = plc.Name;
            report["softwarePath"] = softwarePath;
            report["plcModel"] = cpuModel;
            report["plcOrderNumber"] = orderNumber;
            report["totalTagCount"] = totalTags;
            report["tagTableCount"] = tables.Count;
            report["inputTagTableCount"] = inputTables;
            report["safetySkippedBlockCount"] = blocks.Values.Count(b => b.Kind == PlcInputProgramKind.Safety);
            report["softwareUnits"] = PlcInputUsageAudit.Strings(units.Select(u => u.Name));
            report["coverage"] = new JsonObject
            {
                ["inventoryComplete"] = inventoryComplete, ["nativeTagQueries"] = nativeQueries,
                ["nativeTagReadFailures"] = nativeFailures, ["xmlScannedBlocks"] = xmlScanned,
                ["xmlFailedBlocks"] = xmlFailures, ["xmlUnsupportedBlocks"] = xmlUnsupported,
                ["xmlAbsoluteOccurrences"] = absoluteOccurrences,
                ["xmlExportAttempts"] = xmlExportAttempts, ["safetyXmlExportAttempts"] = safetyXmlExportAttempts,
                ["graphScannedBlocks"] = graphScanned, ["graphFailedBlocks"] = graphFailures,
                ["unclassifiedBlocks"] = blocks.Values.Count(b => b.Kind == PlcInputProgramKind.Unknown),
            };
            return report;
        }

        private static bool ReadInputAuditSafetyRuntime(DeviceItem? cpu, IEnumerable<InputAuditBlock> blocks, List<string> warnings)
        {
            var complete = true;
            void ClassifyMember(string? name, string type)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var candidates = blocks.Where(b => (b.Type == type || type == "DB" && b.Type is "GlobalDB" or "InstanceDB")
                    && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 1)
                    candidates[0].Kind = PlcInputProgramKind.Safety;
                else if (candidates.Count > 1 && candidates.Any(b => b.Kind != PlcInputProgramKind.Safety))
                {
                    foreach (var candidate in candidates.Where(b => b.Kind != PlcInputProgramKind.Safety))
                        candidate.Kind = PlcInputProgramKind.Unknown;
                    complete = false;
                    warnings.Add($"Safety runtime {type} '{name}' could not be uniquely resolved in the block inventory.");
                }
            }

            try
            {
                if (cpu == null) throw new InvalidOperationException("PLC device item is unavailable");
                var safety = cpu.GetService<SafetyAdministration>();
                if (safety != null)
                    foreach (var runtime in safety.RuntimeGroups)
                    {
                        // The runtime F-OB can report SCL. Pre/post processing names are standard code.
                        ClassifyMember(runtime.FOBName, "OB");
                        ClassifyMember(runtime.MainSafetyBlockName, "FB");
                        ClassifyMember(runtime.MainSafetyBlockIDbName, "InstanceDB");
                        ClassifyMember(runtime.InfoDbName, "DB");
                    }
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                foreach (var candidate in blocks.Where(b => b.Type == "OB" && b.Kind == PlcInputProgramKind.Standard))
                    candidate.Kind = PlcInputProgramKind.Unknown;
                complete = false;
                warnings.Add("Safety runtime membership could not be read: " + ex.Message);
            }
            return complete;
        }

        private static string? ReadInputAuditCpuAttribute(DeviceItem? cpu, string attribute, List<string> warnings)
        {
            try
            {
                var result = ((IEngineeringObject?)cpu)?.GetAttribute(attribute)?.ToString();
                if (string.IsNullOrWhiteSpace(result)) warnings.Add($"CPU {attribute} was unavailable.");
                return result;
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                warnings.Add($"CPU {attribute} could not be read: {ex.Message}");
                return null;
            }
        }

        private static string RootSymbol(string[] components) => components[0];

        private static bool NativeSymbolMatches(string? native, string[] components)
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

        private static void ResolveInputCallPin(PlcInputCallPin pin, ILookup<string, InputAuditBlock> targets,
            ILookup<string, InputAuditBlock> qualifiedTargets, ILookup<string, InputAuditBlock> instances)
        {
            if (pin.BlockType is not ("FB" or "FC")) return;
            var name = pin.BlockName?.Replace("\"", "") ?? "";
            if (pin.BlockType == "FB" && name.Length == 0 && pin.InstanceTypeName != null)
                name = pin.InstanceTypeName.Replace("\"", "");
            var candidates = targets[name].Concat(qualifiedTargets[name]).Where(b => b.Type == pin.BlockType).Distinct().ToList();
            if (pin.BlockType == "FB" && pin.InstanceScope == "GlobalVariable" && !string.IsNullOrWhiteSpace(pin.InstanceName))
            {
                var instance = instances[pin.InstanceName!].ToList();
                if (instance.Count > 1) return;
                if (instance.Count == 1)
                {
                    var instanceType = instance[0].InstanceOf?.Replace("\"", "") ?? "";
                    pin.InstanceKind = "InstanceDataBlock";
                    pin.InstanceTypeName = instance[0].InstanceOf;
                    if (name.Length == 0)
                        candidates = targets[instanceType].Concat(qualifiedTargets[instanceType]).Where(b => b.Type == "FB").Distinct().ToList();
                    else
                        candidates = candidates.Where(b => string.Equals(instanceType, b.Name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(instanceType, b.Namespace + "." + b.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                }
            }
            if (candidates.Count != 1) return;
            pin.BlockName = candidates[0].Name;
            pin.BlockNumber = candidates[0].Number;
            if (pin.Direction == null && pin.Pin != null && candidates[0].PinDirections is { } directions)
            {
                var sections = directions[pin.Pin].ToList();
                if (sections.Count == 1) pin.Direction = sections[0];
            }
        }

        private static void ReadNativeInputReferences(PlcTag tag, PlcInputAuditPoint point, string plcName,
            IReadOnlyDictionary<PlcBlock, InputAuditBlock> blocks, ILookup<string, PlcBlock> byPath)
        {
            // On V20 the table can have no service even though its individual tags expose it.
            var service = tag.GetService<CrossReferenceService>()
                ?? throw new InvalidOperationException("PlcTag does not expose CrossReferenceService.");
            var result = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
            var sources = result.Sources.ToList();
            if (sources.Count != 1)
                throw new InvalidOperationException($"AllObjects returned {sources.Count} sources for a single tag.");
            var sourceAddress = sources[0].Address;
            if (PlcInputSpan.TryRange(sourceAddress, out var actual) && actual.Start == point.Span.Start
                && actual.End >= point.Span.End)
                point.Span = actual;

            CrossReferenceFlattener.Flatten(result, point.Table!, out var truncation, out _,
                (entry, rawReference, rawLocation) =>
                {
                    if (PlcInputUsageAudit.IsMetadata(entry.ReferenceType, entry.Access)) return;
                    var reference = (ReferenceObject)rawReference;
                    var location = rawLocation as Location;
                    var referencePath = (entry.ReferencePath ?? "").Replace('\\', '/').Trim('/');
                    var sourcePath = referencePath.Length == 0 ? entry.ReferenceName : $"{referencePath}/{entry.ReferenceName}";
                    var usage = new PlcInputAuditReference
                    {
                        BlockPath = sourcePath, Access = entry.Access, Location = entry.ReferenceLocation,
                        Detail = entry.SourceHierarchy + (string.IsNullOrWhiteSpace(entry.LocationName) ? "" : " / " + entry.LocationName),
                        ConfirmedUsage = entry.ReferenceType == "UsedBy",
                    };
                    if (entry.ReferenceLocation != null
                        && int.TryParse(InputReferenceNetwork.Match(entry.ReferenceLocation).Groups[1].Value, out var network))
                        usage.Network = network;
                    var referredAs = location?.ReferencedAsName?.Trim();
                    usage.Symbol = referredAs;
                    if (referredAs != null && referredAs.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                        && PlcInputSpan.TryParse(referredAs, out var direct))
                    {
                        usage.Address = direct.Canonical;
                        usage.Absolute = true;
                        usage.Symbol = null;
                    }

                    object? user = null;
                    try
                    {
                        user = reference.UnderlyingObject;
                    }
                    catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                    {
                        // Some members have no accessible object. A fully qualified, unique path can
                        // still identify a block from the existing Openness inventory.
                    }
                    var userBlock = user as PlcBlock;
                    var prefix = plcName + "/";
                    if (userBlock == null && sourcePath != null && sourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var qualifiedPath = sourcePath.Substring(prefix.Length);
                        var matches = byPath[qualifiedPath].Distinct().ToList();
                        if (matches.Count == 1) userBlock = matches[0];
                    }
                    if (userBlock == null || !blocks.TryGetValue(userBlock, out var block) || block.Kind == PlcInputProgramKind.Unknown)
                    {
                        point.ReferencesComplete = false;
                        return;
                    }
                    if (block.Kind == PlcInputProgramKind.Safety)
                    {
                        point.HasSafetyUse |= usage.ConfirmedUsage;
                        return;
                    }
                    usage.BlockPath = block.Path;
                    usage.BlockName = block.Name;
                    usage.BlockType = block.Type;
                    usage.BlockNumber = block.Number;
                    usage.Language = block.Language;
                    point.References.Add(usage);
                });
            if (truncation != null) throw new InvalidOperationException(truncation);
        }
    }
}
