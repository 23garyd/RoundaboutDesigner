using System.Collections.Generic;

namespace RoundaboutDesigner.Models
{
    public class CandidateListItem
    {
        public IntersectionCandidate Candidate { get; set; }

        public int Rank => Candidate.Rank;
        public string Name => Candidate.IntersectionName;
        public string ScoreLabel => $"{Candidate.FinalScore:F0}";
        public string TypeLabel => Candidate.Recommendation?.TypeLabel ?? "—";
        public string CostLabel => Candidate.Cost?.TotalFormatted ?? "—";
        public string Summary => $"#{Rank}  ·  {TypeLabel}  ·  {CostLabel}";
        public double Score => Candidate.FinalScore;
    }
}