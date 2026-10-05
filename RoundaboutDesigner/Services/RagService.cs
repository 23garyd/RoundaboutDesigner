using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RoundaboutDesigner.Gemini; // Use your own project name

namespace RoundaboutDesigner.Services // Use your own project name
{
    public class RagDocument
    {
        public string Content { get; set; }
        public float[] Embedding { get; set; }
    }
    public class RagService
    {
        private List<RagDocument> _knowledgeBase = new List<RagDocument>();
        private GeminiService _geminiService;
        public RagService(GeminiService geminiService)
        {
            _geminiService = geminiService;
        }
        // Call this during initialization to load your custom GIS guidelines, metadata, or docs
        public async Task AddDocumentAsync(string text)
        {
            var embedding = await _geminiService.GetEmbeddingAsync(text);
            _knowledgeBase.Add(new RagDocument { Content = text, Embedding = embedding });
        }
        // Search for the most relevant context based on the user's prompt
        public async Task<string> RetrieveContextAsync(string userQuery, int topK = 2)
        {
            if (_knowledgeBase.Count == 0) return "";
            var queryEmbedding = await _geminiService.GetEmbeddingAsync(userQuery);
            // Calculate Cosine Similarity
            var results = _knowledgeBase.Select(doc => new
            {
                Document = doc,
                Similarity = CosineSimilarity(queryEmbedding, doc.Embedding)
            })
            .OrderByDescending(x => x.Similarity)
            .Take(topK);
            return string.Join("\n\n", results.Select(r => r.Document.Content));
        }
        private float CosineSimilarity(float[] vector1, float[] vector2)
        {
            float dotProduct = 0;
            float magnitude1 = 0;
            float magnitude2 = 0;
            for (int i = 0; i < vector1.Length; i++)
            {
                dotProduct += vector1[i] * vector2[i];
                magnitude1 += vector1[i] * vector1[i];
                magnitude2 += vector2[i] * vector2[i];
            }
            return dotProduct / (float)(Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2));
        }
        public void LoadKnowledgeBase(string jsonFilePath)
        {
            if (File.Exists(jsonFilePath))
            {
                string json = File.ReadAllText(jsonFilePath);
                _knowledgeBase = JsonSerializer.Deserialize<List<RagDocument>>(json);
            }
        }
        public void SaveKnowledgeBase(string jsonFilePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFilePath));
            string json = JsonSerializer.Serialize(_knowledgeBase);
            File.WriteAllText(jsonFilePath, json);
        }

        public void Clear() => _knowledgeBase = new List<RagDocument>();

        public int Count => _knowledgeBase?.Count ?? 0;
    }
}
