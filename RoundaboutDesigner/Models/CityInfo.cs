using System.Text.Json.Serialization;

namespace RoundaboutDesigner.Models
{
    public class CityInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("state")] public string State { get; set; }
        [JsonPropertyName("pop")] public int Population { get; set; }
        [JsonPropertyName("placeFp")] public string PlaceFp { get; set; }
        [JsonPropertyName("stateFp")] public string StateFp { get; set; }

        [JsonIgnore]
        public string SizeTier
        {
            get
            {
                if (Population > 2_000_000) return "VeryLarge";
                if (Population > 500_000) return "Large";
                return "Standard";
            }
        }
    }
}