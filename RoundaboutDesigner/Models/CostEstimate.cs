namespace RoundaboutDesigner.Models
{
    public class CostEstimate
    {
        public double LandAcquisition { get; set; }
        public double Construction { get; set; }
        public double UtilityRelocation { get; set; }
        public double Contingency { get; set; }
        public string Notes { get; set; }

        public double Total => LandAcquisition + Construction + UtilityRelocation + Contingency;
        public string TotalFormatted => $"${Total / 1_000_000:F2}M";
    }
}