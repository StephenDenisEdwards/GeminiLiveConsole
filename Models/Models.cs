using Newtonsoft.Json;
using System.Collections.Generic;

namespace GeminiLiveConsole.Models;

public enum IntentType { QUESTION, IMPERATIVE }

public sealed class DetectedIntent
{
    public string Text { get; set; } = "";
    public IntentType Type { get; set; }
    public string Answer { get; set; } = "";
}

public sealed class ToolFunctionCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, object>? Args { get; set; }
}

public sealed class LiveServerMessage
{
    [JsonProperty("serverContent")] public ServerContentBlock? ServerContent { get; set; }
    [JsonProperty("toolCall")] public ToolCallBlock? ToolCall { get; set; }
}

public sealed class ServerContentBlock
{
    [JsonProperty("inputTranscription")] public InputTranscriptionBlock? InputTranscription { get; set; }
}

public sealed class InputTranscriptionBlock
{
    [JsonProperty("text")] public string? Text { get; set; }
}

public sealed class ToolCallBlock
{
    [JsonProperty("functionCalls")] public List<ToolFunctionCall>? FunctionCalls { get; set; }
}

public sealed class ToolFunctionResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public object Response { get; set; } = new { result = "ok" };
}
