using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Models.Configuration;

namespace Oxide.CompilerServices
{
    internal class OxideResolver : MetadataReferenceResolver
    {
        private readonly ILogger _logger;
        private readonly DirectorySettings _directories;
        private readonly string _runtimePath;

        private readonly HashSet<PortableExecutableReference> _referenceCache;

        public OxideResolver(ILogger<OxideResolver> logger, OxideSettings settings)
        {
            _logger = logger;
            _directories = settings.Path;
            _runtimePath = settings.Compiler.FrameworkPath;
            _referenceCache = new HashSet<PortableExecutableReference>();
        }

        public override bool Equals(object? other) => other?.Equals(this) ?? false;

        public override int GetHashCode() => _logger.GetHashCode();

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
        {
            _logger.LogInformation("Resolve: {Reference} {BaseFilePath}", reference, baseFilePath);
            return ImmutableArray<PortableExecutableReference>.Empty;
        }

        public override bool ResolveMissingAssemblies => true;

        public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity) =>
            Reference(definition.Display!);

        public PortableExecutableReference? Reference(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            PortableExecutableReference? reference = _referenceCache.FirstOrDefault(r =>
                Path.GetFileName(r.Display) == name);

            if (reference != null)
            {
                return reference;
            }

            if (name.Equals("System.Private.CoreLib"))
            {
                name = "mscorlib.dll";
            }

            FileInfo fileSystem = new(Path.Combine(_directories.Libraries, name));

            if (fileSystem.Exists)
            {
                reference = MetadataReference.CreateFromFile(fileSystem.FullName);
                _referenceCache.Add(reference);
                return reference;
            }

            fileSystem = new FileInfo(Path.Combine(_runtimePath, name));

            if (fileSystem.Exists)
            {
                reference = MetadataReference.CreateFromFile(fileSystem.FullName);
                _referenceCache.Add(reference);
                return reference;
            }

            _logger.LogError(Constants.CompileEventId, "Unable to find required dependency {name}", name);
            return null;
        }
    }
}
