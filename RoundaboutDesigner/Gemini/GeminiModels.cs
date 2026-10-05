using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace RoundaboutDesigner.Gemini
{
    // --- REQUEST MODELS ---
    public class GeminiRequest
    {
        [JsonPropertyName("contents")]
        public List<Content> Contents { get; set; } = new List<Content>();
        [JsonPropertyName("tools")]
        public List<Tool> Tools { get; set; }
    }
    public class Tool
    {
        [JsonPropertyName("functionDeclarations")]
        public List<FunctionDeclaration> FunctionDeclarations { get; set; } = new List<FunctionDeclaration>();
    }
    public class FunctionDeclaration
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("description")]
        public string Description { get; set; }
        [JsonPropertyName("parameters")]
        public object Parameters { get; set; }
    }
    public class Content
    {
        [JsonPropertyName("role")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Role { get; set; }
        [JsonPropertyName("parts")]
        // CHANGE THIS LINE:
        public List<GeminiPart> Parts { get; set; } = new List<GeminiPart>();
    }
    public class GeminiPart
    {
        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Text { get; set; }

        [JsonPropertyName("functionCall")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FunctionCall FunctionCall { get; set; }

        [JsonPropertyName("functionResponse")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FunctionResponse FunctionResponse { get; set; }

        [JsonPropertyName("thoughtSignature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ThoughtSignature { get; set; }
    }
    // --- RESPONSE MODELS ---
    public class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate> Candidates { get; set; }
    }
    public class Candidate
    {
        [JsonPropertyName("content")]
        public Content Content { get; set; }
    }
    public class FunctionCall
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("args")]
        public Dictionary<string, object> Args { get; set; }
    }
    public class FunctionResponse
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        // Gemini expects "response" to be an object; we wrap a result string.
        [JsonPropertyName("response")]
        public object Response { get; set; }
    }

    public class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; }
        public DateTime Date { get; set; } = DateTime.Now;
        // Stores the actual conversation messages
        public List<Content> History { get; set; } = new List<Content>();
        // Used for displaying in the drop-down menu
        [JsonIgnore]
        public string DisplayText => $"{Title} - {Date:MM/dd HH:mm}";
    }
    public enum GeminiModel
    {
        Flash3Preview,
        FlashLite31Preview,
        FlashLite25
    }
}
