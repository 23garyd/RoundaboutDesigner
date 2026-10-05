using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class IntersectionExtractor
    {
        public async Task<List<IntersectionCandidate>> ExtractAsync(OsmRoadNetwork network)
        {
            // Index: node id -> list of ways using that node
            var nodeToWays = new Dictionary<long, List<OsmWay>>();
            foreach (var way in network.Ways)
            {
                foreach (var nodeId in way.NodeIds)
                {
                    if (!nodeToWays.TryGetValue(nodeId, out var list))
                    {
                        list = new List<OsmWay>();
                        nodeToWays[nodeId] = list;
                    }
                    list.Add(way);
                }
            }

            // Identify nodes that are already part of a roundabout
            var roundaboutNodes = new HashSet<long>();
            foreach (var way in network.Ways)
            {
                if (way.Junction == "roundabout" || way.Junction == "circular")
                {
                    foreach (var nid in way.NodeIds)
                        roundaboutNodes.Add(nid);
                }
            }

            var candidates = new List<IntersectionCandidate>();

            await QueuedTask.Run(() =>
            {
                foreach (var kvp in nodeToWays)
                {
                    long nodeId = kvp.Key;
                    var ways = kvp.Value;

                    if (ways.Count < 3) continue;                  // need 3+ legs
                    if (roundaboutNodes.Contains(nodeId)) continue; // already a roundabout
                    if (!network.Nodes.TryGetValue(nodeId, out var node)) continue;

                    var candidate = new IntersectionCandidate
                    {
                        OsmNodeId = nodeId,
                        Location = MapPointBuilderEx.CreateMapPoint(
                            node.Longitude, node.Latitude, SpatialReferences.WGS84),
                        IntersectionName = BuildIntersectionName(ways),
                        ControlType = InferControlType(node)
                    };

                    foreach (var way in ways)
                    {
                        candidate.Legs.Add(new IntersectionLeg
                        {
                            StreetName = way.Name ?? "(unnamed)",
                            PostedSpeed = way.MaxSpeedMph ?? InferSpeedFromClass(way.Highway),
                            FunctionalClass = way.Highway,
                            LaneCount = way.LaneCount ?? 2,
                            Aadt = 0,             // populated in Phase 6b (HPMS join)
                            BearingDegrees = 0     // populated in Phase 6b (geometry calc)
                        });
                    }

                    candidate.MaxPostedSpeed = candidate.Legs.Max(l => l.PostedSpeed);

                    // --- Phase 6d: pedestrian / bicycle infrastructure rollup ---
                    candidate.HasSidewalks = ways.Any(WayHasSidewalk);
                    candidate.HasBikeInfra = ways.Any(WayHasBikeInfra);

                    candidates.Add(candidate);
                }
            });

            return candidates;
        }

        // Reads OSM way tags if the OsmWay exposes a Tags dictionary; falls back to
        // the typed Highway property otherwise. Safe if Tags is null/absent.
        private static bool TryGetTag(OsmWay way, string key, out string value)
        {
            value = null;
            var tags = way.Tags; // requires OsmWay.Tags; see note if this doesn't compile
            if (tags == null) return false;
            return tags.TryGetValue(key, out value);
        }

        private static bool WayHasSidewalk(OsmWay way)
        {
            if (TryGetTag(way, "sidewalk", out var sw))
            {
                if (sw == "no" || sw == "none") return false;
                return true; // "both", "left", "right", "yes", "separate", etc.
            }
            return false;
        }

        private static bool WayHasBikeInfra(OsmWay way)
        {
            if (way.Highway == "cycleway") return true;
            if (TryGetTag(way, "cycleway", out var cw) && cw != "no" && cw != "none") return true;
            if (TryGetTag(way, "cycleway:left", out var cl) && cl != "no" && cl != "none") return true;
            if (TryGetTag(way, "cycleway:right", out var cr) && cr != "no" && cr != "none") return true;
            return false;
        }

        private static string BuildIntersectionName(List<OsmWay> ways)
        {
            var names = ways
                .Select(w => w.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            if (names.Count == 0) return "Unnamed intersection";
            if (names.Count == 1) return names[0];
            return string.Join(" & ", names);
        }

        private static string InferControlType(OsmNode node)
        {
            if (!node.Tags.TryGetValue("highway", out var v)) return "none";
            switch (v)
            {
                case "traffic_signals": return "signal";
                case "stop": return "stop";
                case "give_way": return "yield";
                default: return "none";
            }
        }

        private static int InferSpeedFromClass(string highwayClass)
        {
            switch (highwayClass)
            {
                case "motorway": return 65;
                case "trunk": return 55;
                case "primary": return 45;
                case "secondary": return 35;
                case "tertiary": return 30;
                case "residential":
                case "unclassified":
                default: return 25;
            }
        }
    }
}