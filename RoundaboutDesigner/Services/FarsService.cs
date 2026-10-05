using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Config;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class FarsResult
    {
        public int CrashesRetrieved { get; set; }
        public int CrashesMatched { get; set; }
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    public class FarsService
    {
        private const double CrashMatchRadiusMeters = 50.0;

        public async Task<FarsResult> AttachFatalCrashesAsync(
            List<IntersectionCandidate> candidates,
            List<(string stateFp, string countyFp)> counties)
        {
            var result = new FarsResult();

            // Build the set of (state, county) integer codes we care about.
            var wanted = new HashSet<(int state, int county)>();
            foreach (var (s, c) in counties)
                if (int.TryParse(s, out int si) && int.TryParse(c, out int ci))
                    wanted.Add((si, ci));

            string folder = AppConfig.Instance.FarsDataFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                result.Diagnostics.Add(
                    $"FARS data folder not found: '{folder}'. Set FarsDataFolder in ApiKeys.json.");
                return result;
            }

            var csvFiles = Directory.GetFiles(folder, "*.csv");
            if (csvFiles.Length == 0)
            {
                result.Diagnostics.Add($"No .csv files found in {folder}.");
                return result;
            }

            var coords = new List<(double lat, double lon)>();

            // Parse CSVs off the UI thread.
            await Task.Run(() =>
            {
                foreach (var file in csvFiles)
                {
                    try
                    {
                        int n = ParseCsvFile(file, wanted, coords);
                        result.Diagnostics.Add($"{Path.GetFileName(file)}: {n} matching crashes");
                    }
                    catch (Exception ex)
                    {
                        result.Diagnostics.Add($"{Path.GetFileName(file)} FAILED: {ex.Message}");
                    }
                }
            });

            // Match crashes to candidates on the MCT.
            await QueuedTask.Run(() =>
            {
                var crashPoints = coords
                    .Select(c => MapPointBuilderEx.CreateMapPoint(c.lon, c.lat, SpatialReferences.WGS84))
                    .ToList();

                foreach (var candidate in candidates)
                {
                    var buffer = GeometryEngine.Instance.GeodesicBuffer(
                        candidate.Location, CrashMatchRadiusMeters) as Polygon;
                    if (buffer == null) continue;

                    candidate.FatalCrashes5Yr =
                        crashPoints.Count(pt => GeometryEngine.Instance.Contains(buffer, pt));
                }

                result.CrashesMatched = candidates.Count(c => c.FatalCrashes5Yr > 0);
            });

            result.CrashesRetrieved = coords.Count;
            return result;
        }

        private static int ParseCsvFile(
            string path, HashSet<(int state, int county)> wanted, List<(double lat, double lon)> coords)
        {
            int matched = 0;

            using var reader = new StreamReader(path);
            string headerLine = reader.ReadLine();
            if (headerLine == null) return 0;

            var header = ParseCsvLine(headerLine);
            int iState = header.FindIndex(h => h.Equals("STATE", StringComparison.OrdinalIgnoreCase));
            int iCounty = header.FindIndex(h => h.Equals("COUNTY", StringComparison.OrdinalIgnoreCase));
            int iLat = header.FindIndex(h => h.Equals("LATITUDE", StringComparison.OrdinalIgnoreCase));
            int iLon = header.FindIndex(h => h.Equals("LONGITUD", StringComparison.OrdinalIgnoreCase));

            if (iState < 0 || iCounty < 0 || iLat < 0 || iLon < 0)
                throw new Exception("Missing one of STATE/COUNTY/LATITUDE/LONGITUD columns.");

            int maxIdx = Math.Max(Math.Max(iState, iCounty), Math.Max(iLat, iLon));

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var f = ParseCsvLine(line);
                if (f.Count <= maxIdx) continue;

                if (!int.TryParse(f[iState], out int st)) continue;
                if (!int.TryParse(f[iCounty], out int co)) continue;
                if (!wanted.Contains((st, co))) continue;

                if (!double.TryParse(f[iLat], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                if (!double.TryParse(f[iLon], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;

                if (lat >= 17 && lat <= 72 && lon >= -180 && lon <= -60)
                {
                    coords.Add((lat, lon));
                    matched++;
                }
            }

            return matched;
        }

        // Minimal CSV field splitter that handles quoted fields and escaped quotes.
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }
    }
}