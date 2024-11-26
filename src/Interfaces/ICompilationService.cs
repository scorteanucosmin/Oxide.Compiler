using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Interfaces;

public interface ICompilationService
{
    public Task Compile(int id, CompilerData data);
}
