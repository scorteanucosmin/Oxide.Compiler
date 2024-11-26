using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Interfaces;

public interface ICompilationService
{
    public ValueTask CompileAsync(int id, CompilerData data, CancellationToken cancellationToken);
}
