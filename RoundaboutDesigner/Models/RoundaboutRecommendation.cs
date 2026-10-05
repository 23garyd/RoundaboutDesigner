namespace RoundaboutDesigner.Models
{
    public enum RoundaboutType
    {
        Mini,
        SingleLane,
        MultiLane
    }

    public class RoundaboutRecommendation
    {
        public RoundaboutType Type { get; set; }
        public int CirculatingLanes { get; set; }
        public double InscribedDiameterFt { get; set; }
        public string Justification { get; set; }

        public string TypeLabel
        {
            get
            {
                switch (Type)
                {
                    case RoundaboutType.Mini: return "Mini-roundabout";
                    case RoundaboutType.SingleLane: return "Single-lane";
                    case RoundaboutType.MultiLane: return "Multi-lane";
                    default: return "Unknown";
                }
            }
        }
    }
}