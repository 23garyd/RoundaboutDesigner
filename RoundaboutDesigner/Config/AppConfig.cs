using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoundaboutDesigner.Config
{
    public class AppConfig
    {
        [JsonPropertyName("GeminiApiKey")]
        public string GeminiApiKey { get; set; }

        [JsonPropertyName("CensusApiKey")]
        public string CensusApiKey { get; set; }

        [JsonPropertyName("FarsDataFolder")]
        public string FarsDataFolder { get; set; }

        [JsonPropertyName("Endpoints")]
        public EndpointConfig Endpoints { get; set; }

        [JsonPropertyName("ScoringDefaults")]
        public ScoringDefaultsConfig ScoringDefaults { get; set; }

        private static AppConfig _instance;
        public static AppConfig Instance
        {
            get
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        private static AppConfig Load()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string configPath = Path.Combine(assemblyDir, "Config", "ApiKeys.json");

            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException(
                    $"Configuration file not found at: {configPath}. " +
                    "Copy Config/ApiKeys.example.json to Config/ApiKeys.json, fill in your own keys, and rebuild.");
            }

            string json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppConfig>(json);
        }
    }

    public class EndpointConfig
    {
        [JsonPropertyName("Overpass")] public string Overpass { get; set; }
        [JsonPropertyName("Hpms")] public string Hpms { get; set; }
        [JsonPropertyName("Fars")] public string Fars { get; set; }
        [JsonPropertyName("Nsi")] public string Nsi { get; set; }
        [JsonPropertyName("CensusAcs")] public string CensusAcs { get; set; }
        [JsonPropertyName("TigerPlaces")] public string TigerPlaces { get; set; }
        [JsonPropertyName("TigerCounties")] public string TigerCounties { get; set; }
    }

    public class ScoringDefaultsConfig
    {
        [JsonPropertyName("Safety")] public double Safety { get; set; }
        [JsonPropertyName("Traffic")] public double Traffic { get; set; }
        [JsonPropertyName("Feasibility")] public double Feasibility { get; set; }
        [JsonPropertyName("Multimodal")] public double Multimodal { get; set; }
        [JsonPropertyName("Destinations")] public double Destinations { get; set; }
        [JsonPropertyName("MinScoreThreshold")] public double MinScoreThreshold { get; set; }
        [JsonPropertyName("MaxIntersectionsCap")] public int MaxIntersectionsCap { get; set; }

        [JsonPropertyName("NsiFootprintBufferM")] public double NsiFootprintBufferM { get; set; } = 45;
        [JsonPropertyName("DestinationRadiusM")] public double DestinationRadiusM { get; set; } = 500;
    }
}