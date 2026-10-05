using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RoundaboutDesigner.Services
{
    /// <summary>
    /// ONE-TIME tool. Reads the prepared source text files, embeds them via RagService,
    /// and writes RoundaboutKB.json and CmfKB.json. Run once, then it's idle.
    /// </summary>
    public class KbBuilder
    {
        private readonly GeminiService _gemini;

        public KbBuilder(GeminiService gemini)
        {
            _gemini = gemini;
        }

        // Build the FHWA guide knowledge base from the decision-chapters text file.
        public async Task BuildFhwaAsync(string sourceTxtPath, string outJsonPath, Action<string> log)
        {
            string text = File.ReadAllText(sourceTxtPath);
            var chunks = ChunkByParagraphs(text, targetChars: 1200, overlap: 150);
            log($"FHWA: {chunks.Count} chunks to embed...");

            var rag = new RagService(_gemini);
            rag.Clear();
            int n = 0;
            foreach (var ch in chunks)
            {
                await rag.AddDocumentAsync(ch);
                if (++n % 25 == 0) log($"  FHWA embedded {n}/{chunks.Count}");
                await Task.Delay(700); // ~85 calls/min, safely under the 100/min free-tier cap
            }
            rag.SaveKnowledgeBase(outJsonPath);
            log($"FHWA KB written ({rag.Count} docs): {outJsonPath}");
        }

        // Build the CMF knowledge base: one document per non-empty line (skip the title block lines).
        public async Task BuildCmfAsync(string sourceTxtPath, string outJsonPath, Action<string> log)
        {
            var lines = File.ReadAllLines(sourceTxtPath)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("Converting", StringComparison.OrdinalIgnoreCase)
                         || l.StartsWith("CRASH MODIFICATION", StringComparison.OrdinalIgnoreCase)
                         || l.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            log($"CMF: {lines.Count} sentences to embed...");
            var rag = new RagService(_gemini);
            rag.Clear();
            int n = 0;
            foreach (var line in lines)
            {
                await rag.AddDocumentAsync(line);
                if (++n % 20 == 0) log($"  CMF embedded {n}/{lines.Count}");
                await Task.Delay(700); // ~85 calls/min, safely under the 100/min free-tier cap
            }
            rag.SaveKnowledgeBase(outJsonPath);
            log($"CMF KB written ({rag.Count} docs): {outJsonPath}");
        }

        // Paragraph-aware chunker with character overlap so context isn't cut mid-thought.
        private static List<string> ChunkByParagraphs(string text, int targetChars, int overlap)
        {
            var paras = text.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();

            var chunks = new List<string>();
            var sb = new System.Text.StringBuilder();

            foreach (var p in paras)
            {
                if (sb.Length + p.Length > targetChars && sb.Length > 0)
                {
                    string chunk = sb.ToString().Trim();
                    chunks.Add(chunk);
                    // start next chunk with the tail of this one for overlap
                    string tail = chunk.Length > overlap ? chunk.Substring(chunk.Length - overlap) : chunk;
                    sb.Clear();
                    sb.Append(tail).Append('\n');
                }
                sb.Append(p).Append('\n');
            }
            if (sb.Length > 0) chunks.Add(sb.ToString().Trim());
            return chunks.Where(c => c.Length > 40).ToList();
        }
    }
}