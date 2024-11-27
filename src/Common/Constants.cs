using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;

namespace Oxide.CompilerServices.Common;

internal static class Constants
{
    internal static readonly EventId StartupEventId = new(1, "Startup");
    internal static readonly EventId ShutdownEventId = new(2, "Shutdown");

    internal static readonly EventId CommandEventId = new(3, "Command");
    internal static readonly EventId CompileEventId = new(4, "Compile");

    internal static readonly EmitOptions PdbEmitOptions = new(debugInformationFormat: DebugInformationFormat.Embedded);
}
