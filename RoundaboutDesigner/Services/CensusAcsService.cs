using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RoundaboutDesigner.Services
{
    public class CensusAcsService
    {
        private static readonly HttpClient _http = new HttpClient();
        private readonly string _baseUrl; // AppConfig.Instance.Endpoints.CensusAcs (https://api.census.gov/data)
        private readonly string _apiKey;  // AppConfig.Instance.CensusApiKey
        private const int Vintage = 2022; // latest acs5

        public CensusAcsService(string baseUrl, string apiKey)
        {
            _baseUrl = baseUrl;
            _apiKey = apiKey;
        }

        // Population-weighted across counties by summing raw worker counts.
        // stateFips: 2-digit (e.g. "33"). countyFips: 3-digit (e.g. "009").
        public async Task<(double WalkBikeShare, double TransitShare)> GetCommuteSharesAsync(
            string stateFips, IEnumerable<string> countyFips)
        {
            long total = 0, walkBike = 0, transit = 0;

            foreach (var county in countyFips)
            {
                string url = $"{_baseUrl}/{Vintage}/acs/acs5" +
                    "?get=B08301_001E,B08301_010E,B08301_018E,B08301_019E" +
                    $"&for=county:{county}&in=state:{stateFips}";
                if (!string.IsNullOrEmpty(_apiKey))
                    url += $"&key={_apiKey}";

                try
                {
                    string resp = await _http.GetStringAsync(url);
                    var rows = JsonSerializer.Deserialize<List<List<string>>>(resp);
                    if (rows == null || rows.Count < 2) continue;

                    var d = rows[1]; // row 0 = header
                    total += SafeLong(d[0]);
                    transit += SafeLong(d[1]); // B08301_010E public transit
                    walkBike += SafeLong(d[2]) + SafeLong(d[3]); // walked + bicycle
                }
                catch
                {
                    // skip a county that fails; baseline still computes from the rest
                }
            }

            if (total == 0) return (0, 0);
            return ((double)walkBike / total, (double)transit / total);
        }

        private static long SafeLong(string s) =>
            long.TryParse(s, out var v) && v >= 0 ? v : 0; // ACS uses negatives as null sentinels
    }
}