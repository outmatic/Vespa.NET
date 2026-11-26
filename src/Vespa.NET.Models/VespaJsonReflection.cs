using System.Text.Json;

namespace Vespa.Models;

internal static class VespaJsonReflection
{
    internal static bool IsEnabled => JsonSerializer.IsReflectionEnabledByDefault;
}
