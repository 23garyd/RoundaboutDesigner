using System.Collections.Generic;
using System.Linq;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// Recommends roundabout type per FHWA guidance based on entering volume,
    /// posted speed, and volume balance across legs.
    /// </summary>
    public class TypeRecommendationService
    {
        // FHWA planning-level daily service volume bands (total entering, veh/day).
        private const int MiniMaxAadt = 10000;
        private const int SingleLaneMaxAadt = 25000;
        private const int MiniMaxSpeed = 30; // mph posted on approaches

        public void RecommendAll(List<IntersectionCandidate> candidates)
        {
            foreach (var c in candidates)
                c.Recommendation = Recommend(c);
        }

        private RoundaboutRecommendation Recommend(IntersectionCandidate c)
        {
            int aadt = c.TotalEnteringAadt;
            int speed = c.MaxPostedSpeed;
            bool imbalanced = c.VolumeBalance > 0 && c.VolumeBalance < 0.4; // one dominant leg

            RoundaboutType type;
            int lanes;
            double diameter;
            string why;

            if (aadt > 0 && aadt <= MiniMaxAadt && speed <= MiniMaxSpeed)
            {
                type = RoundaboutType.Mini;
                lanes = 1;
                diameter = 60; // ~45-90 ft typical mini
                why = $"Low entering volume (~{aadt:N0} veh/day) and low posted speed " +
                      $"({speed} mph) fit a mini-roundabout with a traversable center island.";
            }
            else if (aadt <= SingleLaneMaxAadt || (aadt == 0 && c.LegCount <= 4))
            {
                type = RoundaboutType.SingleLane;
                lanes = 1;
                diameter = 130; // ~105-150 ft typical single-lane
                why = aadt > 0
                    ? $"Moderate entering volume (~{aadt:N0} veh/day) is within single-lane capacity."
                    : "No AADT matched; defaulting to single-lane as the most common configuration. " +
                      "Verify volumes before design.";
            }
            else
            {
                type = RoundaboutType.MultiLane;
                lanes = 2;
                diameter = 180; // ~150-220 ft typical multilane
                why = $"High entering volume (~{aadt:N0} veh/day)" +
                      (imbalanced ? " with an imbalanced flow across legs" : "") +
                      " exceeds single-lane capacity; a multi-lane roundabout is indicated.";
            }

            return new RoundaboutRecommendation
            {
                Type = type,
                CirculatingLanes = lanes,
                InscribedDiameterFt = diameter,
                Justification = why
            };
        }
    }
}