using System;
using System.Collections.Generic;
using System.Linq;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// Computes per-criterion sub-scores (0-1, min-max normalized WITHIN the
    /// study area), applies weights, and produces a 0-100 FinalScore + rank.
    /// Relative normalization means scores are comparable within one run but
    /// NOT across different cities.
    /// </summary>
    public class ScoringService
    {
        public void ScoreAll(List<IntersectionCandidate> candidates, ScoringWeights weights)
        {
            if (candidates == null || candidates.Count == 0) return;

            // Work on a normalized copy of the weights so sliders that don't
            // sum to 1.0 still produce a clean 0-100 scale.
            var w = new ScoringWeights
            {
                Safety = weights.Safety,
                Traffic = weights.Traffic,
                Feasibility = weights.Feasibility,
                Multimodal = weights.Multimodal,
                Destinations = weights.Destinations
            };
            w.Normalize();

            // --- Build the raw criterion values per candidate ---
            // SAFETY: fatal crashes + exposure risk. More of either = better
            // candidate (more to gain from a roundabout).
            var safetyRaw = candidates.ToDictionary(
                c => c,
                c => c.FatalCrashes5Yr * 1000.0 + c.ExposureRisk);

            // TRAFFIC: total entering AADT. Higher volume = stronger candidate,
            // up to the point where multilane is needed (handled in type rec, not here).
            var trafficRaw = candidates.ToDictionary(
                c => c,
                c => (double)c.TotalEnteringAadt);

            // FEASIBILITY: BuildableAreaRatio is already an inverse (fewer/cheaper
            // structures in footprint = higher = more buildable). Use directly.
            var feasibilityRaw = candidates.ToDictionary(
                c => c,
                c => c.BuildableAreaRatio);

            // MULTIMODAL: regional ACS baseline (same for all) PLUS a per-site
            // bonus for actual sidewalk / bike infrastructure on the legs.
            // This is where the flat baseline and per-intersection data combine.
            var multimodalRaw = candidates.ToDictionary(
                c => c,
                c =>
                {
                    double baseline = c.WalkBikeCommuteShare + c.TransitCommuteShare; // 0..~0.5
                    double localBonus = (c.HasSidewalks ? 0.15 : 0.0)
                                      + (c.HasBikeInfra ? 0.15 : 0.0);
                    return baseline + localBonus;
                });

            // DESTINATIONS: nearby trip generators. Schools weighted highest
            // (peak pedestrian conflict), then hospitals, then transit stops.
            var destinationsRaw = candidates.ToDictionary(
                c => c,
                c => c.NearbySchools * 3.0
                   + c.NearbyHospitals * 2.0
                   + c.NearbyTransitStops * 1.0);

            // --- Normalize each criterion 0-1 across the candidate set ---
            Normalize(candidates, safetyRaw, (c, v) => c.SafetyScore = v);
            Normalize(candidates, trafficRaw, (c, v) => c.TrafficScore = v);
            Normalize(candidates, feasibilityRaw, (c, v) => c.FeasibilityScore = v);
            Normalize(candidates, multimodalRaw, (c, v) => c.MultimodalScore = v);
            Normalize(candidates, destinationsRaw, (c, v) => c.DestinationsScore = v);

            // --- Weighted final score, 0-100 ---
            foreach (var c in candidates)
            {
                double final = w.Safety * c.SafetyScore
                             + w.Traffic * c.TrafficScore
                             + w.Feasibility * c.FeasibilityScore
                             + w.Multimodal * c.MultimodalScore
                             + w.Destinations * c.DestinationsScore;

                c.FinalScore = Math.Round(final * 100.0, 1);
                c.ConfidenceLevel = DeriveConfidence(c);
            }

            // --- Rank (1 = highest score) ---
            var ordered = candidates.OrderByDescending(c => c.FinalScore).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Rank = i + 1;
        }

        // Min-max normalize a raw value dictionary into [0,1]; if all values are
        // equal (zero range), everything gets 0.5 to avoid divide-by-zero bias.
        private static void Normalize(
            List<IntersectionCandidate> candidates,
            Dictionary<IntersectionCandidate, double> raw,
            Action<IntersectionCandidate, double> setter)
        {
            double min = raw.Values.Min();
            double max = raw.Values.Max();
            double range = max - min;

            foreach (var c in candidates)
            {
                double norm = range <= 0 ? 0.5 : (raw[c] - min) / range;
                setter(c, norm);
            }
        }

        // Confidence reflects how much real data backed the score, not the score
        // itself. A high score built on zero AADT and zero crashes is low-confidence.
        private static string DeriveConfidence(IntersectionCandidate c)
        {
            int signals = 0;
            if (c.TotalEnteringAadt > 0) signals++;
            if (c.FatalCrashes5Yr > 0 || c.ExposureRisk > 0) signals++;
            if (c.NearbyStructureValue > 0) signals++;
            if (c.NearbySchools + c.NearbyHospitals + c.NearbyTransitStops > 0) signals++;

            if (signals >= 3) return "High";
            if (signals == 2) return "Medium";
            return "Low";
        }
    }
}