using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Vespa.AgentFramework;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SessionIdentity))]
[JsonSerializable(typeof(VespaAgentFileStore.FileTree))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AgentStorageJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(VespaChatHistoryProvider.State))]
internal sealed partial class HistoryStateJsonContext : JsonSerializerContext
{
    // Match the format already written by Microsoft's ProviderSessionState defaults.
    internal static JsonSerializerOptions StateOptions { get; } = new HistoryStateJsonContext(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).Options;
}
