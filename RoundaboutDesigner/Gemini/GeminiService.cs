using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RoundaboutDesigner.Gemini;
using RoundaboutDesigner.Config;

namespace RoundaboutDesigner
{
    public class GeminiService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static string ApiKey => AppConfig.Instance.GeminiApiKey;

        private static readonly Dictionary<GeminiModel, string> Endpoints = new()
        {
            { GeminiModel.Flash3Preview,      "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent" },
            { GeminiModel.FlashLite31Preview, "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent" },
            { GeminiModel.FlashLite25,        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent" }
        };

        private const string EmbeddingEndpoint =
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-2-preview:embedContent";

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var request = new { content = new { parts = new[] { new { text = text } } } };
            string jsonRequest = JsonSerializer.Serialize(request);
            var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync($"{EmbeddingEndpoint}?key={ApiKey}", httpContent);
            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(jsonResponse);
                var values = doc.RootElement.GetProperty("embedding").GetProperty("values");
                return values.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            }
            string errorDetails = await response.Content.ReadAsStringAsync();
            throw new Exception($"Google API Error (Status {response.StatusCode}): {errorDetails}");
        }

        public async Task<GeminiPart> SendMessageAsync(List<Content> conversation, GeminiModel model = GeminiModel.Flash3Preview)
        {
            var request = new GeminiRequest
            {
                Contents = conversation,
                Tools = new List<Tool> { new Tool { FunctionDeclarations = BuildFunctionDeclarations() } }
            };

            string jsonRequest = JsonSerializer.Serialize(request,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var httpContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync($"{Endpoints[model]}?key={ApiKey}", httpContent);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(jsonResponse);
                return geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault();
            }
            string error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Google Error: {error}");
        }

        private static List<FunctionDeclaration> BuildFunctionDeclarations()
        {
            // Helper to keep the schema declarations terse.
            object Obj(Dictionary<string, object> props, List<string> required) =>
                new { type = "OBJECT", properties = props, required };
            object Str(string desc) => new { type = "STRING", description = desc };
            object Num(string desc) => new { type = "NUMBER", description = desc };

            return new List<FunctionDeclaration>
            {
                new FunctionDeclaration
                {
                    Name = "GetCurrentState",
                    Description = "Returns the current analysis state: which study area is loaded, " +
                        "how many candidates exist, whether they have been scored, and any active filter. " +
                        "Call this first if you are unsure what data is available before acting.",
                    Parameters = Obj(new Dictionary<string, object>(), new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "RunFullAnalysis",
                    Description = "Runs the entire pipeline for a city: fetch boundary, road network, " +
                        "intersections, AADT, crashes, structures, commute mode, then score, recommend type, " +
                        "estimate cost, and write the symbolized feature class to the map. SLOW (minutes) and " +
                        "hits external APIs. Only call when the user wants a NEW analysis for a specific city.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "state", Str("Two-letter or full state name, e.g. 'NH'.") },
                        { "city", Str("City/town name exactly as it appears in TIGER, e.g. 'Hanover'.") }
                    }, new List<string> { "state", "city" })
                },
                new FunctionDeclaration
                {
                    Name = "FilterResults",
                    Description = "Filters the current scored candidates by attributes. Applies BOTH an in-memory " +
                        "filter (affects later commands) AND a definition query on the map layer (updates what the " +
                        "planner sees). Use for requests like 'only show candidates under $2M' or 'near schools'.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "maxCost", Num("Max total cost in dollars. Omit if not constraining cost.") },
                        { "minScore", Num("Minimum FinalScore 0-100. Omit if not constraining score.") },
                        { "roundaboutType", Str("One of 'Mini','SingleLane','MultiLane'. Omit if not constraining type.") },
                        { "nearSchools", new { type = "BOOLEAN", description = "If true, only candidates with >=1 nearby school." } },
                        { "minFatalCrashes", Num("Minimum fatal crashes in 5yr. Omit if not constraining.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "SortCandidates",
                    Description = "Re-sorts the current candidates by a field, ascending or descending.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "field", Str("One of 'score','cost','aadt','crashes'.") },
                        { "descending", new { type = "BOOLEAN", description = "True for high-to-low (default), false for low-to-high." } }
                    }, new List<string> { "field" })
                },
                new FunctionDeclaration
                {
                    Name = "AdjustWeights",
                    Description = "Changes the scoring weights and re-scores the EXISTING candidates in place " +
                        "(fast; no re-fetching). Use when the user shifts priorities, e.g. 'care more about safety'. " +
                        "Weights are relative and will be normalized; pass only the ones to change.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "safety", Num("Relative weight for safety.") },
                        { "traffic", Num("Relative weight for traffic.") },
                        { "feasibility", Num("Relative weight for feasibility.") },
                        { "multimodal", Num("Relative weight for multimodal.") },
                        { "destinations", Num("Relative weight for destinations.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "GetCandidateDetails",
                    Description = "Returns the full attribute set for one candidate, identified by rank " +
                        "(1 = best) or by name. Use to answer 'tell me about candidate 3' or 'the one at Main & Elm'.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "rank", Num("The candidate's rank (1-based). Use this OR name.") },
                        { "name", Str("Part of the intersection name to match. Use this OR rank.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "SummarizeResults",
                    Description = "Returns aggregate stats over the current candidates: count, how many cleared " +
                        "the threshold, score range, cost range, and type breakdown. Use for 'give me an overview'.",
                    Parameters = Obj(new Dictionary<string, object>(), new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "ZoomToCandidate",
                    Description = "Zooms/pans the active map to one candidate, identified by rank or name.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "rank", Num("The candidate's rank (1-based). Use this OR name.") },
                        { "name", Str("Part of the intersection name to match. Use this OR rank.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "ExplainScore",
                    Description = "Explains how one candidate's FinalScore was built from the weighted sub-scores " +
                        "(safety, traffic, feasibility, multimodal, destinations). Identified by rank or name.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "rank", Num("The candidate's rank (1-based). Use this OR name.") },
                        { "name", Str("Part of the intersection name to match. Use this OR rank.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "ExplainRecommendation",
                    Description = "Explains WHY a candidate received its recommended roundabout type, " +
                        "citing the FHWA Roundabouts Informational Guide. Identified by rank or name.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "rank", Num("Candidate rank (1-based). Use this OR name.") },
                        { "name", Str("Part of the intersection name. Use this OR rank.") }
                    }, new List<string>())
                },
                new FunctionDeclaration
                {
                    Name = "EstimateBenefit",
                    Description = "Returns the expected crash-reduction benefit of converting a candidate " +
                        "to its recommended roundabout type, citing a specific CMF/CRF and its star rating " +
                        "from the FHWA CMF Clearinghouse. Identified by rank or name.",
                    Parameters = Obj(new Dictionary<string, object>
                    {
                        { "rank", Num("Candidate rank (1-based). Use this OR name.") },
                        { "name", Str("Part of the intersection name. Use this OR rank.") }
                    }, new List<string>())
                }
            };
        }
    }
}