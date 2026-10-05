using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class CityCatalogService
    {
        private Dictionary<string, List<CityInfo>> _byState;

        public void Load()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Knowledgebase", "UsCitiesByState.json");

            if (!File.Exists(path))
            {
                _byState = new Dictionary<string, List<CityInfo>>();
                return;
            }

            string json = File.ReadAllText(path);
            _byState = JsonSerializer.Deserialize<Dictionary<string, List<CityInfo>>>(json)
                       ?? new Dictionary<string, List<CityInfo>>();
        }

        public List<string> GetStates()
        {
            if (_byState == null) Load();
            return _byState.Keys.OrderBy(k => k).ToList();
        }

        public List<CityInfo> GetCities(string stateAbbrev)
        {
            if (_byState == null) Load();
            if (string.IsNullOrEmpty(stateAbbrev) || !_byState.ContainsKey(stateAbbrev))
                return new List<CityInfo>();
            return _byState[stateAbbrev].OrderByDescending(c => c.Population).ToList();
        }
    }
}