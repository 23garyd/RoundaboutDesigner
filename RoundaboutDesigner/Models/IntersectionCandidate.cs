using System.Collections.Generic;
using ArcGIS.Core.Geometry;

namespace RoundaboutDesigner.Models
{
    public class IntersectionLeg
    {
        public string StreetName { get; set; }
        public int Aadt { get; set; }
        public int PostedSpeed { get; set; }
        public string FunctionalClass { get; set; }
        public int LaneCount { get; set; }
        public double BearingDegrees { get; set; }
    }

    public class IntersectionCandidate
    {
        public long OsmNodeId { get; set; }
        public MapPoint Location { get; set; }
        public string IntersectionName { get; set; }
        public List<IntersectionLeg> Legs { get; set; } = new List<IntersectionLeg>();
        public int LegCount => Legs?.Count ?? 0;
        public string ControlType { get; set; }

        public int TotalEnteringAadt { get; set; }
        public int MaxLegAadt { get; set; }
        public double VolumeBalance { get; set; }
        public int MaxPostedSpeed { get; set; }

        public int FatalCrashes5Yr { get; set; }
        public double ExposureRisk { get; set; }

        public double BuildableAreaRatio { get; set; }
        public double NearbyStructureValue { get; set; }

        public bool HasSidewalks { get; set; }
        public bool HasBikeInfra { get; set; }
        public double WalkBikeCommuteShare { get; set; }
        public double TransitCommuteShare { get; set; }

        public int NearbySchools { get; set; }
        public int NearbyHospitals { get; set; }
        public int NearbyTransitStops { get; set; }

        public double SafetyScore { get; set; }
        public double TrafficScore { get; set; }
        public double FeasibilityScore { get; set; }
        public double MultimodalScore { get; set; }
        public double DestinationsScore { get; set; }
        public double FinalScore { get; set; }
        public int Rank { get; set; }

        public RoundaboutRecommendation Recommendation { get; set; }
        public CostEstimate Cost { get; set; }
        public string ConfidenceLevel { get; set; }
    }
}