using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
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

        internal JsonObject AuditPlcInputUsage(string softwarePath, PlcInputUsageAudit audit)
        {
            const string tool = "AuditPlcInputUsage";
            var plc = RequirePlc(tool, softwarePath);
            var warnings = new List<string>();
            var inventoryComplete = true;
            var points = new List<PlcInputAuditPoint>();
            var tables = new List<(PlcTagTable Table, string Path)>();
            var blocks = new Dictionary<PlcBlock, string>();
            var units = SelectSoftwareUnits(plc, "", tool, out var unitFailure);
            if (unitFailure != null)
            {
                inventoryComplete = false;
                warnings.Add("Software unit enumeration failed: " + unitFailure);
            }

            void CollectScope(string prefix, PlcTagTableGroup? tagGroup, PlcBlockGroup? blockGroup)
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
                    var path = prefix.Length == 0 ? "Program blocks" : prefix;
                    foreach (var (groupPath, block) in EnumerateUnitBlocks(
                        blockGroup ?? throw new InvalidOperationException("missing block group"), path))
                        blocks.Add(block, $"{groupPath}/{block.Name}");
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    inventoryComplete = false;
                    warnings.Add($"{prefix}: block enumeration failed: {ex.Message}");
                }
            }

            CollectScope("", plc.TagTableGroup, plc.BlockGroup);
            foreach (var unit in units)
                CollectScope($"{SoftwareUnitsRoot}/{unit.Name}", unit.TagTableGroup, unit.BlockGroup);

            int totalTags = 0, inputTables = 0, nativeQueries = 0, nativeFailures = 0;
            foreach (var (table, tablePath) in tables)
            {
                bool hasInput = false;
                try
                {
                    foreach (var tag in table.Tags)
                    {
                        totalTags++;
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
                            Name = tag.Name, Address = address, Span = span, Table = tablePath,
                            SoftwareUnit = tablePath.StartsWith(SoftwareUnitsRoot + "/", StringComparison.Ordinal)
                                ? tablePath.Split('/')[1] : null,
                        };
                        nativeQueries++;
                        try
                        {
                            ReadNativeInputReferences(tag, point, plc.Name, blocks);
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
                    }
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    inventoryComplete = false;
                    warnings.Add($"{tablePath}: tag enumeration failed: {ex.Message}");
                }
                if (hasInput) inputTables++;
            }

            int xmlScanned = 0, xmlFailures = 0, xmlUnsupported = 0, absoluteOccurrences = 0;
            var untagged = new Dictionary<PlcInputSpan, PlcInputAuditPoint>();
            var temp = Path.Combine(Path.GetTempPath(), "tia-input-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var file = new FileInfo(Path.Combine(temp, "block.xml"));
            try
            {
                foreach (var (block, path) in blocks.Where(p => p.Key is CodeBlock).Select(p => (p.Key, p.Value)))
                {
                    try
                    {
                        if (block.IsKnowHowProtected)
                            throw new InvalidOperationException("the block body is know-how protected");
                        block.Export(file, ExportOptions.None);
                        var issues = new List<string>();
                        var matches = PlcInputSimaticMl.Scan(XDocument.Load(file.FullName), issues);
                        xmlScanned++;
                        if (issues.Count > 0)
                        {
                            xmlUnsupported++;
                            warnings.Add($"{path}: {string.Join("; ", issues.Distinct())}.");
                        }
                        foreach (var match in matches)
                        {
                            var owners = points.Where(p => p.Span.Overlaps(match.Span)).ToList();
                            if (audit.InRange(match.Span) && !owners.Any(p => p.Span.Contains(match.Span)))
                            {
                                if (!untagged.TryGetValue(match.Span, out var standalone))
                                {
                                    standalone = new PlcInputAuditPoint { Address = match.Span.Canonical, Span = match.Span };
                                    untagged.Add(match.Span, standalone);
                                }
                                owners.Add(standalone);
                            }
                            if (owners.Count == 0) continue;
                            absoluteOccurrences++;
                            var reference = new PlcInputAuditReference
                            {
                                BlockPath = path, BlockType = block.GetType().Name, BlockNumber = block.Number,
                                Language = block.ProgrammingLanguage.ToString(), Access = match.Access,
                                Location = match.Site, Network = match.Network, Address = match.Span.Canonical,
                                Origin = "simaticML", Absolute = true, ConfirmedUsage = match.Confirmed,
                            };
                            foreach (var owner in owners) owner.References.Add(reference);
                        }
                    }
                    catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                    {
                        xmlFailures++;
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

            var cpu = GetSoftwareContainer(softwarePath)?.Parent as DeviceItem;
            var cpuModel = ReadInputAuditCpuAttribute(cpu, "TypeName", warnings);
            var orderNumber = ReadInputAuditCpuAttribute(cpu, "OrderNumber", warnings);
            var report = audit.Summarize(points.Concat(untagged.Values), warnings, inventoryComplete,
                xmlFailures == 0 && xmlUnsupported == 0);
            report["projectName"] = _project?.Name;
            report["plcName"] = plc.Name;
            report["softwarePath"] = softwarePath;
            report["plcModel"] = cpuModel;
            report["plcOrderNumber"] = orderNumber;
            report["totalTagCount"] = totalTags;
            report["tagTableCount"] = tables.Count;
            report["inputTagTableCount"] = inputTables;
            report["softwareUnits"] = PlcInputUsageAudit.Strings(units.Select(u => u.Name));
            report["coverage"] = new JsonObject
            {
                ["inventoryComplete"] = inventoryComplete, ["nativeTagQueries"] = nativeQueries,
                ["nativeTagReadFailures"] = nativeFailures, ["xmlScannedBlocks"] = xmlScanned,
                ["xmlFailedBlocks"] = xmlFailures, ["xmlUnsupportedBlocks"] = xmlUnsupported,
                ["xmlAbsoluteOccurrences"] = absoluteOccurrences,
            };
            return report;
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

        private static void ReadNativeInputReferences(PlcTag tag, PlcInputAuditPoint point, string plcName,
            IReadOnlyDictionary<PlcBlock, string> blocks)
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
                    if (referredAs != null && referredAs.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                        && PlcInputSpan.TryParse(referredAs, out var direct))
                    {
                        usage.Address = direct.Canonical;
                        usage.Absolute = true;
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
                        var matches = blocks.Where(p => p.Value.Equals(qualifiedPath, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 1) userBlock = matches[0].Key;
                    }
                    if (userBlock != null)
                    {
                        if (blocks.TryGetValue(userBlock, out var blockPath)) usage.BlockPath = blockPath;
                        usage.BlockType = userBlock.GetType().Name;
                        usage.BlockNumber = userBlock.Number;
                        usage.Language = userBlock.ProgrammingLanguage.ToString();
                    }
                    point.References.Add(usage);
                });
            if (truncation != null) throw new InvalidOperationException(truncation);
        }
    }
}
