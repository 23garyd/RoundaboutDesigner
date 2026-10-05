using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Config;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class HpmsSegment
    {
        public Polyline Geometry { get; set; }
        public int Aadt { get; set; }
    }

    public class HpmsService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        // AADT attribute name in the HPMS service. Verify against the FeatureServer's
        // field list — the federal HPMS schema uses "aadt"; adjust if your service differs.
        private const string AadtField = "AADT";

        // How close (meters) an HPMS segment must be to an intersection to count.
        private const double SearchRadiusMeters = 40.0;

        /// <summary>
        /// Fetches HPMS segments for the study area and attaches AADT-derived
        /// attributes to each candidate. Returns the number of segments retrieved.
        /// </summary>
        public async Task<int> AttachAadtAsync(List<IntersectionCandidate> candidates, Polygon studyArea)
        {
            var segments = await FetchSegmentsAsync(studyArea);

            await QueuedTask.Run(() =>
            {
                foreach (var candidate in candidates)
                {
                    var buffer = GeometryEngine.Instance.GeodesicBuffer(
                        candidate.Location, SearchRadiusMeters) as Polygon;
                    if (buffer == null) continue;

                    var nearbyAadts = new List<int>();
                    foreach (var seg in segments)
                    {
                        if (seg.Aadt <= 0) continue;
                        if (GeometryEngine.Instance.Intersects(buffer, seg.Geometry))
                            nearbyAadts.Add(seg.Aadt);
                    }

                    if (nearbyAadts.Count == 0) continue;

                    candidate.MaxLegAadt = nearbyAadts.Max();
                    candidate.TotalEnteringAadt = nearbyAadts.Distinct().Sum();

                    if (candidate.MaxLegAadt > 0)
                        candidate.VolumeBalance = (double)nearbyAadts.Min() / candidate.MaxLegAadt;

                    // Exposure proxy for the Safety criterion: volume × intersection complexity
                    candidate.ExposureRisk = candidate.TotalEnteringAadt * candidate.LegCount;
                }
            });

            return segments.Count;
        }

        private async Task<List<HpmsSegment>> FetchSegmentsAsync(Polygon studyArea)
        {
            double xmin = 0, ymin = 0, xmax = 0, ymax = 0;
            await QueuedTask.Run(() =>
            {
                var e = studyArea.Extent;
                xmin = e.XMin; ymin = e.YMin; xmax = e.XMax; ymax = e.YMax;
            });

            var segments = new List<HpmsSegment>();
            int offset = 0;
            const int pageSize = 1000;
            bool more = true;
            string baseUrl = AppConfig.Instance.Endpoints.Hpms;

            while (more)
            {
                string geom = Uri.EscapeDataString(
                    $"{{\"xmin\":{xmin},\"ymin\":{ymin},\"xmax\":{xmax},\"ymax\":{ymax}}}");
                string url = $"{baseUrl}/query?geometry={geom}" +
                             $"&geometryType=esriGeometryEnvelope&inSR=4326&outSR=4326" +
                             $"&spatialRel=esriSpatialRelIntersects" +
                             $"&outFields={AadtField}&returnGeometry=true" +
                             $"&resultOffset={offset}&resultRecordCount={pageSize}&f=json";

                string json = await _http.GetStringAsync(url);

                int countThisPage = 0;
                await QueuedTask.Run(() =>
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("features", out var features)) return;

                    foreach (var f in features.EnumerateArray())
                    {
                        countThisPage++;

                        int aadt = 0;
                        if (f.TryGetProperty("attributes", out var attrs) &&
                            attrs.TryGetProperty(AadtField, out var aadtEl) &&
                            aadtEl.ValueKind == JsonValueKind.Number)
                        {
                            aadt = (int)aadtEl.GetDouble();
                        }

                        if (f.TryGetProperty("geometry", out var g) &&
                            g.TryGetProperty("paths", out var paths))
                        {
                            var line = ParseEsriPolyline(paths);
                            if (line != null)
                                segments.Add(new HpmsSegment { Geometry = line, Aadt = aadt });
                        }
                    }
                });

                more = countThisPage == pageSize;
                offset += pageSize;
                if (offset > 50000) break; // safety cap
            }

            return segments;
        }

        private static Polyline ParseEsriPolyline(JsonElement paths)
        {
            var builder = new PolylineBuilderEx(SpatialReferences.WGS84);
            foreach (var path in paths.EnumerateArray())
            {
                var pts = new List<Coordinate2D>();
                foreach (var pt in path.EnumerateArray())
                    pts.Add(new Coordinate2D(pt[0].GetDouble(), pt[1].GetDouble()));
                if (pts.Count >= 2)
                    builder.AddPart(pts);
            }
            return builder.ToGeometry() as Polyline;
        }
    }
}