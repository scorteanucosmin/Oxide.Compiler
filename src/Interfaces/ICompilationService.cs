using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Interfaces;

public interface ICompilationService
{
    public Task CompileAsync(int id, CompilerData data, CancellationToken cancellationToken);
}
