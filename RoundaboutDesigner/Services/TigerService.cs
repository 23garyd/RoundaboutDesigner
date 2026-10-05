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
    public class TigerService
    {
        private static readonly HttpClient _http = new HttpClient();

        private const int PLACES_LAYER = 4;    // Incorporated Places (Current)
        private const int COUSUB_LAYER = 1;    // County Subdivisions (Current)
        private const int COUNTIES_LAYER = 1;  // Counties layer in the State_County service — verify

        public async Task<StudyArea> GetCityBoundaryAsync(CityInfo city)
        {
            if (city == null) throw new ArgumentNullException(nameof(city));
            if (string.IsNullOrEmpty(city.StateFp))
                throw new InvalidOperationException("CityInfo must have StateFp populated.");

            JsonElement? geometry = await QueryPlaceLayerAsync(PLACES_LAYER, city.StateFp, city.Name);
            if (geometry == null)
                geometry = await QueryPlaceLayerAsync(COUSUB_LAYER, city.StateFp, city.Name);

            if (geometry == null)
                throw new InvalidOperationException(
                    $"No TIGER boundary found for {city.Name} (STATE={city.StateFp}) in " +
                    $"Incorporated Places or County Subdivisions. Verify the name matches BASENAME.");

            Polygon polygon = null;
            await QueuedTask.Run(() => { polygon = ParseEsriPolygon(geometry.Value); });

            return new StudyArea
            {
                State = city.State,
                CityName = city.Name,
                Population = city.Population,
                PlaceFp = city.PlaceFp,
                Boundary = polygon
            };
        }

        /// <summary>
        /// Returns the (stateFp, countyFp) pairs for counties intersecting the study area.
        /// FARS and ACS both key off county FIPS.
        /// </summary>
        public async Task<List<(string stateFp, string countyFp)>> GetCountiesAsync(Polygon studyArea)
        {
            double xmin = 0, ymin = 0, xmax = 0, ymax = 0;
            await QueuedTask.Run(() =>
            {
                var e = studyArea.Extent;
                xmin = e.XMin; ymin = e.YMin; xmax = e.XMax; ymax = e.YMax;
            });

            string baseUrl = AppConfig.Instance.Endpoints.TigerCounties;
            string geom = Uri.EscapeDataString(
                $"{{\"xmin\":{xmin},\"ymin\":{ymin},\"xmax\":{xmax},\"ymax\":{ymax}}}");
            string url = $"{baseUrl}/{COUNTIES_LAYER}/query?geometry={geom}" +
                         $"&geometryType=esriGeometryEnvelope&inSR=4326" +
                         $"&spatialRel=esriSpatialRelIntersects" +
                         $"&outFields=STATE,COUNTY&returnGeometry=false&f=json";

            string json = await _http.GetStringAsync(url);
            var result = new List<(string, string)>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var f in features.EnumerateArray())
                {
                    if (!f.TryGetProperty("attributes", out var attrs)) continue;
                    string st = ReadFips(attrs, "STATE");
                    string co = ReadFips(attrs, "COUNTY");
                    if (!string.IsNullOrEmpty(st) && !string.IsNullOrEmpty(co))
                        result.Add((st, co));
                }
            }

            return result.Distinct().ToList();
        }

        private static string ReadFips(JsonElement attrs, string field)
        {
            if (!attrs.TryGetProperty(field, out var el)) return null;
            return el.ValueKind == JsonValueKind.Number
                ? el.GetInt32().ToString()
                : el.GetString();
        }

        private async Task<JsonElement?> QueryPlaceLayerAsync(int layer, string stateFp, string baseName)
        {
            string baseUrl = AppConfig.Instance.Endpoints.TigerPlaces;
            string safeName = baseName.Replace("'", "''");
            string where = Uri.EscapeDataString($"STATE='{stateFp}' AND BASENAME='{safeName}'");
            string url = $"{baseUrl}/{layer}/query?where={where}" +
                         $"&outFields=BASENAME,STATE&returnGeometry=true&outSR=4326&f=json";

            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("features", out var features) &&
                features.GetArrayLength() > 0 &&
                features[0].TryGetProperty("geometry", out var geom))
            {
                return geom.Clone();
            }
            return null;
        }

        private static Polygon ParseEsriPolygon(JsonElement geomElement)
        {
            var rings = geomElement.GetProperty("rings");
            var builder = new PolygonBuilderEx(SpatialReferences.WGS84);

            foreach (var ring in rings.EnumerateArray())
            {
                var pointList = new List<Coordinate2D>();
                foreach (var pt in ring.EnumerateArray())
                    pointList.Add(new Coordinate2D(pt[0].GetDouble(), pt[1].GetDouble()));
                builder.AddPart(pointList);
            }

            return builder.ToGeometry();
        }
    }
}