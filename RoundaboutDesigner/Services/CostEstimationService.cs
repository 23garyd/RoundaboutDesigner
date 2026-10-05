using System;
using System.Collections.Generic;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// Planning-level cost estimate. Deliberately coarse — order-of-magnitude
    /// for screening, NOT a design estimate. All unit costs are constants here
    /// so they can later move to config if desired.
    /// </summary>
    public class CostEstimationService
    {
        private const double ConstructionPerSqFt = 55.0;   // earthwork, paving, islands, signs, lighting
        private const double UtilityRelocationPerLeg = 75000.0;
        private const double ContingencyRate = 0.30;       // 30% planning contingency
        // Fraction of nearby structure value assumed to require ROW acquisition.
        private const double RowAcquisitionFraction = 0.10;

        public void EstimateAll(List<IntersectionCandidate> candidates)
        {
            foreach (var c in candidates)
                c.Cost = Estimate(c);
        }

        private CostEstimate Estimate(IntersectionCandidate c)
        {
            double diameter = c.Recommendation?.InscribedDiameterFt ?? 130;
            double radius = diameter / 2.0;
            double footprintSqFt = Math.PI * radius * radius;

            // Multilane widens the circulating roadway → bump the footprint cost.
            double laneFactor = (c.Recommendation?.CirculatingLanes ?? 1) >= 2 ? 1.6 : 1.0;

            double construction = footprintSqFt * ConstructionPerSqFt * laneFactor;
            double land = c.NearbyStructureValue * RowAcquisitionFraction;
            double utilities = c.LegCount * UtilityRelocationPerLeg;
            double subtotal = construction + land + utilities;
            double contingency = subtotal * ContingencyRate;

            return new CostEstimate
            {
                Construction = Math.Round(construction),
                LandAcquisition = Math.Round(land),
                UtilityRelocation = Math.Round(utilities),
                Contingency = Math.Round(contingency),
                Notes = $"{c.Recommendation?.TypeLabel ?? "Single-lane"}, " +
                        $"{c.LegCount} legs, ~{footprintSqFt:N0} sq ft footprint. " +
                        $"Planning-level only; land cost proxied from NSI structure value."
            };
        }
    }
}