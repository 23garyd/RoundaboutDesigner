using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Config;

namespace RoundaboutDesigner.Services
{
    public class OsmWay
    {
        public long Id { get; set; }
        public List<long> NodeIds { get; set; } = new List<long>();
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

        public string Highway => Tags.TryGetValue("highway", out var v) ? v : null;
        public string Name => Tags.TryGetValue("name", out var v) ? v : null;
        public string Junction => Tags.TryGetValue("junction", out var v) ? v : null;

        public int? MaxSpeedMph
        {
            get
            {
                if (!Tags.TryGetValue("maxspeed", out var v)) return null;
                var digits = new string(v.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out int speed) ? (int?)speed : null;
            }
        }

        public int? LaneCount
        {
            get
            {
                if (!Tags.TryGetValue("lanes", out var v)) return null;
                return int.TryParse(v, out int n) ? (int?)n : null;
            }
        }
    }

    public class OsmNode
    {
        public long Id { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public string Highway => Tags.TryGetValue("highway", out var v) ? v : null;
    }

    public class OsmRoadNetwork
    {
        public List<OsmWay> Ways { get; set; } = new List<OsmWay>();
        public Dictionary<long, OsmNode> Nodes { get; set; } = new Dictionary<long, OsmNode>();
    }

    public class OsmService
    {
        private const double GeneralizationToleranceDeg = 0.001;

        // Tried in order. The configured endpoint goes first, then these mirrors.
        private static readonly string[] FallbackEndpoints = new[]
        {
            "https://overpass.kumi.systems/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://overpass-api.de/api/interpreter"
        };

        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            client.DefaultRequestHeaders.Add("User-Agent", "RoundaboutDesigner-ArcGISPro-AddIn/1.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }

        public async Task<OsmRoadNetwork> FetchRoadNetworkAsync(Polygon studyAreaBoundary)
        {
            string polyParam = await BuildPolyParameterAsync(studyAreaBoundary);

            string query =
                "[out:json][timeout:90];" +
                "(" +
                "way[\"highway\"]" +
                "[\"highway\"!~\"^(motorway_link|trunk_link|footway|service|driveway|track|path|cycleway|pedestrian|steps|construction|proposed|raceway)$\"]" +
                "(poly:\"" + polyParam + "\");" +
                ");" +
                "out body;" +
                ">;" +
                "out skel qt;";

            // Build the endpoint try-order: configured first, then the mirrors.
            var endpoints = new List<string> { AppConfig.Instance.Endpoints.Overpass };
            foreach (var fb in FallbackEndpoints)
                if (!endpoints.Contains(fb)) endpoints.Add(fb);

            Exception lastError = null;

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("data", query)
                    });

                    var response = await _http.PostAsync(endpoint, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        return ParseOverpassResponse(json);
                    }

                    // 504/503/502/429 are transient — record and try the next mirror.
                    lastError = new Exception(
                        $"{endpoint} returned {(int)response.StatusCode} ({response.ReasonPhrase})");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await Task.Delay(1500); // brief pause before the next mirror
            }

            throw new Exception(
                $"All Overpass mirrors failed or timed out. Last error: {lastError?.Message}");
        }

        private async Task<string> BuildPolyParameterAsync(Polygon boundary)
        {
            var sb = new StringBuilder();
            int pointCount = 0;

            await QueuedTask.Run(() =>
            {
                var simplified = GeometryEngine.Instance.Generalize(
                    boundary, GeneralizationToleranceDeg, true) as Polygon;

                if (simplified == null || simplified.PartCount == 0)
                    throw new InvalidOperationException("Could not simplify study area boundary.");

                var outerRing = simplified.Parts[0];
                foreach (var segment in outerRing)
                {
                    var pt = segment.StartPoint;
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append($"{pt.Y:F6} {pt.X:F6}");
                    pointCount++;
                }
            });

            if (pointCount < 3)
                throw new InvalidOperationException(
                    $"Boundary simplified to fewer than 3 points (got {pointCount}). " +
                    "Reduce GeneralizationToleranceDeg.");

            return sb.ToString();
        }

        private static OsmRoadNetwork ParseOverpassResponse(string json)
        {
            var network = new OsmRoadNetwork();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("elements", out var elements))
                return network;

            foreach (var elem in elements.EnumerateArray())
            {
                string type = elem.GetProperty("type").GetString();
                long id = elem.GetProperty("id").GetInt64();

                if (type == "node")
                {
                    var node = new OsmNode { Id = id };
                    if (elem.TryGetProperty("lat", out var lat)) node.Latitude = lat.GetDouble();
                    if (elem.TryGetProperty("lon", out var lon)) node.Longitude = lon.GetDouble();
                    if (elem.TryGetProperty("tags", out var tags))
                        node.Tags = ParseTags(tags);
                    network.Nodes[id] = node;
                }
                else if (type == "way")
                {
                    var way = new OsmWay { Id = id };
                    if (elem.TryGetProperty("nodes", out var nodes))
                    {
                        foreach (var n in nodes.EnumerateArray())
                            way.NodeIds.Add(n.GetInt64());
                    }
                    if (elem.TryGetProperty("tags", out var tags))
                        way.Tags = ParseTags(tags);
                    network.Ways.Add(way);
                }
            }

            return network;
        }

        private static Dictionary<string, string> ParseTags(JsonElement tagsElement)
        {
            var dict = new Dictionary<string, string>();
            foreach (var prop in tagsElement.EnumerateObject())
                dict[prop.Name] = prop.Value.GetString();
            return dict;
        }
    }
}