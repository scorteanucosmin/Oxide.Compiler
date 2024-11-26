using Microsoft.Extensions.Logging;

namespace Oxide.CompilerServices.Common;

public static class Constants
{
    public static readonly EventId StartupEventId = new(1, "Startup");
    public static readonly EventId ShutdownEventId = new(2, "Shutdown");

    public static readonly EventId CommandEventId = new(3, "Command");
    public static readonly EventId CompileEventId = new(4, "Compile");
}
