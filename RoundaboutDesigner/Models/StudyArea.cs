using ArcGIS.Core.Geometry;

namespace RoundaboutDesigner.Models
{
    public class StudyArea
    {
        public string State { get; set; }
        public string CityName { get; set; }
        public int Population { get; set; }
        public string PlaceFp { get; set; }
        public Polygon Boundary { get; set; }
        public Envelope Extent => Boundary?.Extent;

        public string DisplayName => $"{CityName}, {State}";
    }
}