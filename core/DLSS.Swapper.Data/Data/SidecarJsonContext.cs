using System.Text.Json.Serialization;

namespace DLSS_Swapper.Data;

/// <summary>
/// Source-generated serialisation for the mirror sidecar, matching how the rest of the data layer
/// avoids reflection-based JSON.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OriginalsStore.SidecarRecord))]
internal partial class SidecarJsonContext : JsonSerializerContext
{
}
