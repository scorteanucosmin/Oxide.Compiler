using Newtonsoft.Json;

namespace Oxide.CompilerServices.Models.Compiler;

[Serializable]
public class CompilationResult
{
    public string Name { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte[] Symbols { get; set; } = Array.Empty<byte>();

    [NonSerialized]
    public int Success, Failed;

    [JsonConstructor]
    public CompilationResult()
    {

    }
}
