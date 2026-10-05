using System;
using System.Collections.Generic;

namespace RoundaboutDesigner.Models
{
    public class AnalysisResult
    {
        public StudyArea StudyArea { get; set; }
        public ScoringWeights WeightsUsed { get; set; }
        public DateTime RunAt { get; set; } = DateTime.Now;
        public int RequestedCount { get; set; }
        public int TotalIntersectionsExamined { get; set; }
        public int TotalPassingThreshold { get; set; }
        public double ThresholdUsed { get; set; }
        public List<IntersectionCandidate> Candidates { get; set; } = new List<IntersectionCandidate>();
        public string OutputFeatureClassPath { get; set; }

        public bool ResultsTruncatedByThreshold => TotalPassingThreshold < RequestedCount;
    }
}