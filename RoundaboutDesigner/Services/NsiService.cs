using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class NsiStructure
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string OccType { get; set; }   // e.g. "EDU1", "COM6", "RES1"
        public double ValStruct { get; set; } // replacement value proxy
    }

    public class NsiService
    {
        private static readonly HttpClient _http = new HttpClient();
        private readonly string _endpoint;

        public NsiService(string endpoint)
        {
            _endpoint = endpoint; // AppConfig.Instance.Endpoints.Nsi
        }

        // Fetch all structures inside the AOI polygon ONCE.
        public async Task<List<NsiStructure>> FetchStructuresAsync(Polygon boundary)
        {
            string geoJson = BuildGeoJsonPolygon(boundary);
            var content = new StringContent(geoJson, Encoding.UTF8, "application/json");

            // POST polygon body, request a GeoJSON FeatureCollection back
            HttpResponseMessage resp = await _http.PostAsync($"{_endpoint}?fmt=fc", content);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();

            return ParseStructures(json);
        }

        private static string BuildGeoJsonPolygon(Polygon boundary)
        {
            // Use the outer ring points (lon = X, lat = Y for WGS84)
            var pts = boundary.Points.ToList();

            var coords = new List<string>(pts.Count + 1);
            foreach (var p in pts)
            {
                coords.Add(
                    $"[{p.X.ToString(CultureInfo.InvariantCulture)}," +
                    $"{p.Y.ToString(CultureInfo.InvariantCulture)}]");
            }
            // GeoJSON rings must be closed (first == last)
            if (pts.Count > 0 &&
                (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y))
            {
                coords.Add(
                    $"[{pts[0].X.ToString(CultureInfo.InvariantCulture)}," +
                    $"{pts[0].Y.ToString(CultureInfo.InvariantCulture)}]");
            }

            string ring = string.Join(",", coords);
            return "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\"," +
                   "\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[" + ring + "]]}," +
                   "\"properties\":{}}]}";
        }

        private static List<NsiStructure> ParseStructures(string json)
        {
            var list = new List<NsiStructure>();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("features", out var features))
                return list;

            foreach (var feat in features.EnumerateArray())
            {
                try
                {
                    var props = feat.GetProperty("properties");
                    var coords = feat.GetProperty("geometry").GetProperty("coordinates");

                    double lon = coords[0].GetDouble();
                    double lat = coords[1].GetDouble();

                    string occ = props.TryGetProperty("occtype", out var o)
                        ? (o.GetString() ?? "") : "";

                    double val = 0;
                    if (props.TryGetProperty("val_struct", out var v) &&
                        v.ValueKind == JsonValueKind.Number)
                    {
                        val = v.GetDouble();
                    }

                    list.Add(new NsiStructure { Lon = lon, Lat = lat, OccType = occ, ValStruct = val });
                }
                catch { /* skip malformed feature */ }
            }
            return list;
        }

        // Per-candidate matching. MUST be called inside QueuedTask.Run (GeometryEngine).
        public void AttachStructureAttrs(
            List<IntersectionCandidate> candidates,
            List<NsiStructure> structures,
            double footprintBufferM = 45,
            double destinationRadiusM = 500)
        {
            foreach (var c in candidates)
            {
                if (c.Location == null) continue;

                var footprint = GeometryEngine.Instance.GeodesicBuffer(c.Location, footprintBufferM) as Polygon;
                var destRadius = GeometryEngine.Instance.GeodesicBuffer(c.Location, destinationRadiusM) as Polygon;

                double valInFootprint = 0;
                int cntInFootprint = 0;
                int schools = 0, hospitals = 0;

                foreach (var s in structures)
                {
                    var sp = MapPointBuilderEx.CreateMapPoint(s.Lon, s.Lat, SpatialReferences.WGS84);

                    if (footprint != null && GeometryEngine.Instance.Contains(footprint, sp))
                    {
                        valInFootprint += s.ValStruct;
                        cntInFootprint++;
                    }
                    if (destRadius != null && GeometryEngine.Instance.Contains(destRadius, sp))
                    {
                        if (!string.IsNullOrEmpty(s.OccType))
                        {
                            if (s.OccType.StartsWith("EDU", StringComparison.OrdinalIgnoreCase)) schools++;
                            else if (s.OccType.Equals("COM6", StringComparison.OrdinalIgnoreCase)) hospitals++;
                        }
                    }
                }

                c.NearbyStructureValue = valInFootprint;
                c.BuildableAreaRatio = 1.0 / (1.0 + cntInFootprint); // inverse: fewer structures = more buildable
                c.NearbySchools = schools;
                c.NearbyHospitals = hospitals;
                // NearbyTransitStops comes from OSM (see RunAnalysis note).
            }
        }
    }
}