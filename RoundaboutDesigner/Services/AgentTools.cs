using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// Executes agent function calls against the current analysis state.
    /// Holds the candidate list + weights between turns so the agent can
    /// filter, re-sort, and re-score without re-running the pipeline.
    /// </summary>
    public class AgentTools
    {
        // --- State carried across turns ---
        public List<IntersectionCandidate> AllCandidates { get; set; } = new();
        public List<IntersectionCandidate> CurrentView { get; set; } = new();
        public ScoringWeights Weights { get; set; }
        public StudyArea StudyArea { get; set; }
        public double Threshold { get; set; }
        public string LayerName { get; set; }
        public RagService FhwaKb { get; set; }
        public RagService CmfKb { get; set; }

        // Callback into the view model to run the full pipeline for a city.
        // Returns a short status string. Set by the view model.
        public Func<string, string, Task<string>> RunFullAnalysisAsync { get; set; }

        public bool HasResults => AllCandidates != null && AllCandidates.Count > 0;

        // Routes a function call to its handler and returns a result string.
        public async Task<string> ExecuteAsync(string functionName, Dictionary<string, object> args)
        {
            try
            {
                switch (functionName)
                {
                    case "GetCurrentState": return GetCurrentState();
                    case "RunFullAnalysis": return await RunFull(args);
                    case "FilterResults": return await FilterResults(args);
                    case "SortCandidates": return SortCandidates(args);
                    case "AdjustWeights": return AdjustWeights(args);
                    case "GetCandidateDetails": return GetCandidateDetails(args);
                    case "SummarizeResults": return SummarizeResults();
                    case "ZoomToCandidate": return await ZoomToCandidate(args);
                    case "ExplainScore": return ExplainScore(args);
                    case "ExplainRecommendation": return await ExplainRecommendation(args);
                    case "EstimateBenefit": return await EstimateBenefit(args);

                    default: return $"ERROR: unknown function '{functionName}'.";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR executing {functionName}: {ex.Message}";
            }
        }

        // --- Handlers ---

        private string GetCurrentState()
        {
            if (!HasResults)
                return "No analysis loaded. No study area, no candidates. " +
                       "Call RunFullAnalysis with a city to begin.";

            return $"Study area: {StudyArea?.DisplayName ?? "unknown"}. " +
                   $"{AllCandidates.Count} total candidates, all scored. " +
                   $"Current view shows {CurrentView.Count} after filters/sorts. " +
                   $"Weights — safety {Weights.Safety:P0}, traffic {Weights.Traffic:P0}, " +
                   $"feasibility {Weights.Feasibility:P0}, multimodal {Weights.Multimodal:P0}, " +
                   $"destinations {Weights.Destinations:P0}. Threshold {Threshold:N0}.";
        }

        private async Task<string> RunFull(Dictionary<string, object> args)
        {
            if (RunFullAnalysisAsync == null)
                return "ERROR: pipeline runner not wired.";
            string state = GetString(args, "state");
            string city = GetString(args, "city");
            if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(city))
                return "ERROR: need both state and city.";
            return await RunFullAnalysisAsync(state, city);
        }

        private async Task<string> FilterResults(Dictionary<string, object> args)
        {
            if (!HasResults) return "No candidates to filter. Run an analysis first.";

            double? maxCost = GetDouble(args, "maxCost");
            double? minScore = GetDouble(args, "minScore");
            string rbType = GetString(args, "roundaboutType");
            bool? nearSchools = GetBool(args, "nearSchools");
            double? minCrashes = GetDouble(args, "minFatalCrashes");

            IEnumerable<IntersectionCandidate> q = AllCandidates;
            if (maxCost.HasValue) q = q.Where(c => (c.Cost?.Total ?? double.MaxValue) <= maxCost.Value);
            if (minScore.HasValue) q = q.Where(c => c.FinalScore >= minScore.Value);
            if (nearSchools == true) q = q.Where(c => c.NearbySchools > 0);
            if (minCrashes.HasValue) q = q.Where(c => c.FatalCrashes5Yr >= minCrashes.Value);
            if (!string.IsNullOrWhiteSpace(rbType) &&
                Enum.TryParse<RoundaboutType>(rbType, true, out var t))
                q = q.Where(c => c.Recommendation != null && c.Recommendation.Type == t);

            CurrentView = q.OrderBy(c => c.Rank).ToList();

            // Build a matching definition query on the map layer (both targets).
            var clauses = new List<string>();
            if (maxCost.HasValue) clauses.Add($"CostTotal <= {maxCost.Value.ToString(CultureInfo.InvariantCulture)}");
            if (minScore.HasValue) clauses.Add($"FinalScore >= {minScore.Value.ToString(CultureInfo.InvariantCulture)}");
            if (nearSchools == true) clauses.Add("DestS > 0"); // proxy; schools feed destinations
            if (minCrashes.HasValue) clauses.Add($"Fatal5Yr >= {minCrashes.Value.ToString(CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrWhiteSpace(rbType) && Enum.TryParse<RoundaboutType>(rbType, true, out var t2))
                clauses.Add($"RbType = '{TypeLabel(t2)}'");
            string defQuery = clauses.Count > 0 ? string.Join(" AND ", clauses) : "";

            await ApplyDefinitionQuery(defQuery);

            return $"Filter applied. {CurrentView.Count} of {AllCandidates.Count} candidates match. " +
                   (CurrentView.Count > 0
                        ? "Top match: " + Describe(CurrentView[0])
                        : "No candidates match these criteria.");
        }

        private string SortCandidates(Dictionary<string, object> args)
        {
            if (CurrentView.Count == 0 && HasResults) CurrentView = AllCandidates.ToList();
            if (CurrentView.Count == 0) return "No candidates to sort.";

            string field = (GetString(args, "field") ?? "score").ToLowerInvariant();
            bool desc = GetBool(args, "descending") ?? true;

            Func<IntersectionCandidate, double> key = field switch
            {
                "cost" => c => c.Cost?.Total ?? 0,
                "aadt" => c => c.TotalEnteringAadt,
                "crashes" => c => c.FatalCrashes5Yr,
                _ => c => c.FinalScore
            };
            CurrentView = (desc ? CurrentView.OrderByDescending(key) : CurrentView.OrderBy(key)).ToList();

            return $"Sorted {CurrentView.Count} candidates by {field} ({(desc ? "high to low" : "low to high")}). " +
                   "Top: " + Describe(CurrentView[0]);
        }

        private string AdjustWeights(Dictionary<string, object> args)
        {
            if (!HasResults) return "No candidates to re-score. Run an analysis first.";

            Weights.Safety = GetDouble(args, "safety") ?? Weights.Safety;
            Weights.Traffic = GetDouble(args, "traffic") ?? Weights.Traffic;
            Weights.Feasibility = GetDouble(args, "feasibility") ?? Weights.Feasibility;
            Weights.Multimodal = GetDouble(args, "multimodal") ?? Weights.Multimodal;
            Weights.Destinations = GetDouble(args, "destinations") ?? Weights.Destinations;

            new ScoringService().ScoreAll(AllCandidates, Weights);
            CurrentView = AllCandidates.OrderBy(c => c.Rank).ToList();

            return $"Re-scored with new weights (safety {Weights.Safety:P0}, traffic {Weights.Traffic:P0}, " +
                   $"feasibility {Weights.Feasibility:P0}, multimodal {Weights.Multimodal:P0}, " +
                   $"destinations {Weights.Destinations:P0}). New top candidate: " + Describe(CurrentView[0]);
        }

        private string GetCandidateDetails(Dictionary<string, object> args)
        {
            var c = Resolve(args);
            if (c == null) return "Could not find that candidate.";
            var sb = new StringBuilder();
            sb.AppendLine($"Rank {c.Rank}: {c.IntersectionName} (confidence {c.ConfidenceLevel}).");
            sb.AppendLine($"FinalScore {c.FinalScore:F0}. Control: {c.ControlType}, {c.LegCount} legs, " +
                          $"max posted speed {c.MaxPostedSpeed} mph.");
            sb.AppendLine($"Traffic: {c.TotalEnteringAadt:N0} total entering AADT. " +
                          $"Safety: {c.FatalCrashes5Yr} fatal crashes (5yr).");
            sb.AppendLine($"Destinations nearby: {c.NearbySchools} schools, {c.NearbyHospitals} hospitals, " +
                          $"{c.NearbyTransitStops} transit stops. Sidewalks: {c.HasSidewalks}, bike infra: {c.HasBikeInfra}.");
            if (c.Recommendation != null)
                sb.AppendLine($"Recommended: {c.Recommendation.TypeLabel}, {c.Recommendation.CirculatingLanes} " +
                              $"circulating lane(s), ~{c.Recommendation.InscribedDiameterFt:F0} ft. " +
                              $"{c.Recommendation.Justification}");
            if (c.Cost != null)
                sb.AppendLine($"Estimated cost {c.Cost.TotalFormatted} " +
                              $"(land {c.Cost.LandAcquisition:C0}, construction {c.Cost.Construction:C0}, " +
                              $"utilities {c.Cost.UtilityRelocation:C0}, contingency {c.Cost.Contingency:C0}).");
            return sb.ToString();
        }

        private string SummarizeResults()
        {
            if (!HasResults) return "No candidates yet.";
            int passed = AllCandidates.Count(c => c.FinalScore >= Threshold);
            double minS = AllCandidates.Min(c => c.FinalScore);
            double maxS = AllCandidates.Max(c => c.FinalScore);
            double minC = AllCandidates.Where(c => c.Cost != null).Select(c => c.Cost.Total).DefaultIfEmpty(0).Min();
            double maxC = AllCandidates.Where(c => c.Cost != null).Select(c => c.Cost.Total).DefaultIfEmpty(0).Max();
            var byType = AllCandidates.Where(c => c.Recommendation != null)
                .GroupBy(c => c.Recommendation.TypeLabel)
                .Select(g => $"{g.Count()} {g.Key}").ToList();

            return $"{AllCandidates.Count} candidates in {StudyArea?.DisplayName}. " +
                   $"{passed} cleared the threshold of {Threshold:N0}. " +
                   $"Scores {minS:F0}–{maxS:F0}. Costs {minC / 1e6:F2}M–{maxC / 1e6:F2}M. " +
                   $"Types: {string.Join(", ", byType)}.";
        }

        private async Task<string> ZoomToCandidate(Dictionary<string, object> args)
        {
            var c = Resolve(args);
            if (c == null) return "Could not find that candidate.";
            if (c.Location == null) return "That candidate has no location geometry.";

            await QueuedTask.Run(() =>
            {
                var mv = MapView.Active;
                if (mv == null) return;
                var buffer = GeometryEngine.Instance.GeodesicBuffer(c.Location, 200) as Polygon;
                mv.ZoomTo(buffer?.Extent ?? c.Location.Extent, TimeSpan.FromSeconds(1));
            });
            return $"Zoomed to rank {c.Rank}: {c.IntersectionName}.";
        }

        private string ExplainScore(Dictionary<string, object> args)
        {
            var c = Resolve(args);
            if (c == null) return "Could not find that candidate.";
            var w = Weights;
            double cs = w.Safety * c.SafetyScore, ct = w.Traffic * c.TrafficScore,
                   cf = w.Feasibility * c.FeasibilityScore, cm = w.Multimodal * c.MultimodalScore,
                   cd = w.Destinations * c.DestinationsScore;
            return $"{c.IntersectionName} scored {c.FinalScore:F0}. Weighted contributions (each 0–100 scaled): " +
                   $"safety {cs * 100:F0}, traffic {ct * 100:F0}, feasibility {cf * 100:F0}, " +
                   $"multimodal {cm * 100:F0}, destinations {cd * 100:F0}. " +
                   $"Raw sub-scores 0–1: safety {c.SafetyScore:F2}, traffic {c.TrafficScore:F2}, " +
                   $"feasibility {c.FeasibilityScore:F2}, multimodal {c.MultimodalScore:F2}, " +
                   $"destinations {c.DestinationsScore:F2}.";
        }

        private async Task<string> ExplainRecommendation(Dictionary<string, object> args)
        {
            var c = Resolve(args);
            if (c == null) return "Could not find that candidate.";
            if (c.Recommendation == null) return "That candidate has no recommendation yet.";
            if (FhwaKb == null) return "FHWA knowledge base not loaded.";

            string query = $"{c.Recommendation.TypeLabel} roundabout selection criteria " +
                           $"entering volume {c.TotalEnteringAadt} posted speed {c.MaxPostedSpeed} " +
                           $"{c.LegCount} legs sizing";
            string context = await FhwaKb.RetrieveContextAsync(query, topK: 2);

            return $"Recommendation for {c.IntersectionName}: {c.Recommendation.TypeLabel}. " +
                   $"Tool justification: {c.Recommendation.Justification}\n\n" +
                   $"Relevant FHWA guidance:\n{context}";
        }
        private async Task<string> EstimateBenefit(Dictionary<string, object> args)
        {
            var c = Resolve(args);
            if (c == null) return "Could not find that candidate.";
            if (CmfKb == null) return "CMF knowledge base not loaded.";

            // Build a query from the candidate's prior control + recommended lane count.
            string prior = (c.ControlType ?? "").ToLowerInvariant().Contains("signal")
                ? "signalized" : "stop-controlled";
            string lanes = (c.Recommendation?.CirculatingLanes ?? 1) >= 2 ? "multi-lane" : "single-lane";
            string query = $"converting {prior} intersection to {lanes} roundabout crash reduction";

            string context = await CmfKb.RetrieveContextAsync(query, topK: 2);
            return $"Safety benefit for {c.IntersectionName} ({prior} → {lanes} roundabout):\n{context}\n" +
                   $"This candidate currently has {c.FatalCrashes5Yr} fatal crash(es) recorded in the last 5 years.";
        }

        // --- Helpers ---

        private IntersectionCandidate Resolve(Dictionary<string, object> args)
        {
            double? rank = GetDouble(args, "rank");
            string name = GetString(args, "name");
            var pool = (CurrentView.Count > 0 ? CurrentView : AllCandidates);
            if (rank.HasValue)
                return pool.FirstOrDefault(c => c.Rank == (int)rank.Value)
                    ?? AllCandidates.FirstOrDefault(c => c.Rank == (int)rank.Value);
            if (!string.IsNullOrWhiteSpace(name))
                return pool.FirstOrDefault(c => c.IntersectionName != null &&
                    c.IntersectionName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            return null;
        }

        private async Task ApplyDefinitionQuery(string whereClause)
        {
            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                var layer = map?.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == LayerName);
                layer?.SetDefinitionQuery(whereClause ?? "");
            });
        }

        private static string Describe(IntersectionCandidate c) =>
            $"rank {c.Rank} {c.IntersectionName}, score {c.FinalScore:F0}, " +
            $"{c.Recommendation?.TypeLabel ?? "?"}, {c.Cost?.TotalFormatted ?? "?"}.";

        private static string TypeLabel(RoundaboutType t) => t switch
        {
            RoundaboutType.Mini => "Mini-roundabout",
            RoundaboutType.SingleLane => "Single-lane",
            RoundaboutType.MultiLane => "Multi-lane",
            _ => "Unknown"
        };

        // JSON arg coercion — Gemini args arrive as JsonElement via the deserializer.
        private static string GetString(Dictionary<string, object> a, string k)
        {
            if (a == null || !a.TryGetValue(k, out var v) || v == null) return null;
            if (v is JsonElement e) return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
            return v.ToString();
        }
        private static double? GetDouble(Dictionary<string, object> a, string k)
        {
            if (a == null || !a.TryGetValue(k, out var v) || v == null) return null;
            if (v is JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
                if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            }
            return Convert.ToDouble(v);
        }
        private static bool? GetBool(Dictionary<string, object> a, string k)
        {
            if (a == null || !a.TryGetValue(k, out var v) || v == null) return null;
            if (v is JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.True) return true;
                if (e.ValueKind == JsonValueKind.False) return false;
                if (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out var b)) return b;
                return null;
            }
            return Convert.ToBoolean(v);
        }
    }
}