using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RoundaboutDesigner.Config;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// ONE-TIME generator. Builds a national UsCitiesByState.json from TIGER
    /// (geometry/names) + ACS B01003 (population). Run once via a temporary
    /// trigger, confirm the JSON, then remove the trigger. Not a runtime path.
    /// </summary>
    public class CatalogGenerator
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        private const int PLACES_LAYER = 4;
        private const int COUSUB_LAYER = 1;

        // 50 states + DC. Key = display name, value = 2-digit state FIPS.
        private static readonly Dictionary<string, string> StateFips = new()
        {
            {"Alabama","01"}, {"Alaska","02"}, {"Arizona","04"}, {"Arkansas","05"},
            {"California","06"}, {"Colorado","08"}, {"Connecticut","09"}, {"Delaware","10"},
            {"District of Columbia","11"}, {"Florida","12"}, {"Georgia","13"}, {"Hawaii","15"},
            {"Idaho","16"}, {"Illinois","17"}, {"Indiana","18"}, {"Iowa","19"},
            {"Kansas","20"}, {"Kentucky","21"}, {"Louisiana","22"}, {"Maine","23"},
            {"Maryland","24"}, {"Massachusetts","25"}, {"Michigan","26"}, {"Minnesota","27"},
            {"Mississippi","28"}, {"Missouri","29"}, {"Montana","30"}, {"Nebraska","31"},
            {"Nevada","32"}, {"New Hampshire","33"}, {"New Jersey","34"}, {"New Mexico","35"},
            {"New York","36"}, {"North Carolina","37"}, {"North Dakota","38"}, {"Ohio","39"},
            {"Oklahoma","40"}, {"Oregon","41"}, {"Pennsylvania","42"}, {"Rhode Island","44"},
            {"South Carolina","45"}, {"South Dakota","46"}, {"Tennessee","47"}, {"Texas","48"},
            {"Utah","49"}, {"Vermont","50"}, {"Virginia","51"}, {"Washington","53"},
            {"West Virginia","54"}, {"Wisconsin","55"}, {"Wyoming","56"}
        };

        public async Task GenerateAsync(string outputJsonPath, Action<string> log)
        {
            var catalog = new Dictionary<string, List<CityInfo>>();

            foreach (var (stateName, ss) in StateFips)
            {
                log($"Processing {stateName} ({ss})...");
                try
                {
                    // ACS population, whole state in one call per geography.
                    var placePop = await FetchAcsPopAsync(ss, isPlace: true);
                    var cousubPop = await FetchAcsPopAsync(ss, isPlace: false);

                    // TIGER names/FIPS (no geometry needed for the catalog — boundaries
                    // are fetched at analysis time by GetCityBoundaryAsync).
                    var places = await FetchTigerNamesAsync(ss, PLACES_LAYER);
                    var cousubs = await FetchTigerNamesAsync(ss, COUSUB_LAYER);

                    var list = BuildCities(stateName, ss, places, placePop, cousubs, cousubPop);
                    catalog[stateName] = list;
                    log($"  {stateName}: {list.Count} localities");
                }
                catch (Exception ex)
                {
                    log($"  {stateName} FAILED: {ex.Message} (skipped)");
                    catalog[stateName] = new List<CityInfo>();
                }

                await Task.Delay(300); // be polite to the services
            }

            string json = JsonSerializer.Serialize(catalog,
                new JsonSerializerOptions { WriteIndented = false });
            Directory.CreateDirectory(Path.GetDirectoryName(outputJsonPath));
            File.WriteAllText(outputJsonPath, json);
            log($"DONE. {catalog.Sum(kv => kv.Value.Count)} localities across {catalog.Count} states → {outputJsonPath}");
        }

        // ---- ACS: returns FIPS -> population for all places or all cousubs in a state ----
        private async Task<Dictionary<string, long>> FetchAcsPopAsync(string ss, bool isPlace)
        {
            string key = AppConfig.Instance.CensusApiKey;
            string baseUrl = AppConfig.Instance.Endpoints.CensusAcs;
            string forGeo = isPlace ? "place:*" : "county%20subdivision:*";
            string inClause = isPlace ? $"state:{ss}" : $"state:{ss}%20county:*";
            string url = $"{baseUrl}/2022/acs/acs5?get=NAME,B01003_001E&for={forGeo}&in={inClause}&key={key}";

            var dict = new Dictionary<string, long>();
            string resp = await _http.GetStringAsync(url);
            var rows = JsonSerializer.Deserialize<List<List<string>>>(resp);
            if (rows == null || rows.Count < 2) return dict;

            var hdr = rows[0];
            int popIdx = hdr.IndexOf("B01003_001E");
            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                // Place rows end with [..., state, place]; cousub rows end with [..., state, county, cousub].
                string fips = isPlace ? r[r.Count - 1] : (r[r.Count - 2] + r[r.Count - 1]);
                if (popIdx >= 0 && long.TryParse(r[popIdx], out var pop) && pop >= 0)
                    dict[fips] = pop;
            }
            return dict;
        }

        // ---- TIGER: returns list of (fips, basename) for a layer, paged ----
        private async Task<List<(string fips, string basename)>> FetchTigerNamesAsync(string ss, int layer)
        {
            string baseUrl = AppConfig.Instance.Endpoints.TigerPlaces;
            var results = new List<(string, string)>();
            int offset = 0;
            const int pageSize = 1000;
            bool more = true;

            // GEOID for places = state(2)+place(5); for cousubs = state(2)+county(3)+cousub(5).
            // We pull GEOID + BASENAME and derive the join key to match the ACS FIPS.
            while (more)
            {
                string where = Uri.EscapeDataString($"STATE='{ss}'");
                string url = $"{baseUrl}/{layer}/query?where={where}" +
                             $"&outFields=BASENAME,GEOID,STATE&returnGeometry=false" +
                             $"&resultOffset={offset}&resultRecordCount={pageSize}&f=json";

                string json = await _http.GetStringAsync(url);
                int count = 0;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("features", out var feats))
                    {
                        foreach (var f in feats.EnumerateArray())
                        {
                            count++;
                            if (!f.TryGetProperty("attributes", out var a)) continue;
                            string basename = a.TryGetProperty("BASENAME", out var bn) ? bn.GetString() : null;
                            string geoid = a.TryGetProperty("GEOID", out var gd)
                                ? (gd.ValueKind == JsonValueKind.Number ? gd.GetInt64().ToString() : gd.GetString())
                                : null;
                            if (string.IsNullOrEmpty(basename) || string.IsNullOrEmpty(geoid)) continue;

                            // Join key must match the ACS FIPS:
                            //  place GEOID = SSPPPPP  -> place part = last 5
                            //  cousub GEOID = SSCCCZZZZZ -> we built ACS key as county+cousub = last 8
                            string joinFips = layer == PLACES_LAYER
                                ? geoid.Substring(geoid.Length - 5)        // place: 5-digit
                                : geoid.Substring(geoid.Length - 8);       // cousub: county(3)+cousub(5)
                            results.Add((joinFips, basename));
                        }
                    }
                }
                more = count == pageSize;
                offset += pageSize;
                if (offset > 60000) break; // safety
                await Task.Delay(150);
            }
            return results;
        }

        // ---- Join + dedupe into CityInfo ----
        private List<CityInfo> BuildCities(
            string stateName, string ss,
            List<(string fips, string basename)> places, Dictionary<string, long> placePop,
            List<(string fips, string basename)> cousubs, Dictionary<string, long> cousubPop)
        {
            var byName = new Dictionary<string, CityInfo>(StringComparer.OrdinalIgnoreCase);

            // Incorporated Places first (they win on name collision).
            foreach (var (fips, basename) in places)
            {
                placePop.TryGetValue(fips, out long pop);
                if (!byName.ContainsKey(basename))
                {
                    byName[basename] = new CityInfo
                    {
                        Name = basename,
                        State = stateName,
                        StateFp = ss,
                        PlaceFp = fips,
                        Population = (int)Math.Min(pop, int.MaxValue)
                    };
                }
            }

            // County Subdivisions only where no Place of that name exists (New England fix).
            foreach (var (fips, basename) in cousubs)
            {
                if (byName.ContainsKey(basename)) continue;
                cousubPop.TryGetValue(fips, out long pop);
                byName[basename] = new CityInfo
                {
                    Name = basename,
                    State = stateName,
                    StateFp = ss,
                    PlaceFp = fips,   // for cousubs this is county+cousub; GetCityBoundaryAsync matches by BASENAME so this is informational
                    Population = (int)Math.Min(pop, int.MaxValue)
                };
            }

            // Drop zero-population noise and sort by population descending (your UI ranks by pop).
            return byName.Values
                .Where(c => c.Population > 0)
                .OrderByDescending(c => c.Population)
                .ToList();
        }
    }
}