using Microsoft.Extensions.Logging;

namespace Oxide.CompilerServices.Models.Configuration
{
    public class LogSettings
    {
        public string FileName { get; set; } = Program.DEBUG ? "compiler-debug.log" : "compiler.log";

        public LogLevel Level { get; set; } = LogLevel.Debug;
    }
}
