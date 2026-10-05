namespace RoundaboutDesigner.Models
{
    public class ScoringWeights
    {
        public double Safety { get; set; }
        public double Traffic { get; set; }
        public double Feasibility { get; set; }
        public double Multimodal { get; set; }
        public double Destinations { get; set; }

        public double Sum => Safety + Traffic + Feasibility + Multimodal + Destinations;

        public void Normalize()
        {
            double s = Sum;
            if (s <= 0) return;
            Safety /= s;
            Traffic /= s;
            Feasibility /= s;
            Multimodal /= s;
            Destinations /= s;
        }

        public static ScoringWeights FromConfig()
        {
            var d = Config.AppConfig.Instance.ScoringDefaults;
            return new ScoringWeights
            {
                Safety = d.Safety,
                Traffic = d.Traffic,
                Feasibility = d.Feasibility,
                Multimodal = d.Multimodal,
                Destinations = d.Destinations
            };
        }
    }
}