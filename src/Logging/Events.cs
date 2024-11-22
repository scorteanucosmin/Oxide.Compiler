using Microsoft.Extensions.Logging;

namespace Oxide.CompilerServices.Logging;

public static class Events
{
    public static readonly EventId Startup = new(1, "Startup");
    public static readonly EventId Shutdown = new(2, "Shutdown");

    public static readonly EventId Command = new(3, "Command");
    public static readonly EventId Compile = new(4, "Compile");


}
