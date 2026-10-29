using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Types.Compilation;
using Oxide.CompilerServices.Types.Configuration;

namespace Oxide.CompilerServices.Common;

public class OxideResolver : MetadataReferenceResolver
{
    private readonly ILogger _logger;
    private readonly AppConfiguration _appConfiguration;
    private readonly string _runtimePath;
    private readonly Dictionary<string, AssemblyMetadata> _references = new(StringComparer.OrdinalIgnoreCase);
    public override bool ResolveMissingAssemblies => true;

    public OxideResolver(ILogger<OxideResolver> logger, AppConfiguration appConfiguration)
    {
        _logger = logger;
        _appConfiguration = appConfiguration;
        _runtimePath = appConfiguration.GetCompilerConfiguration().FrameworkPath;
    }

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath,
        MetadataReferenceProperties properties) => ImmutableArray<PortableExecutableReference>.Empty;

    public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference metadataReference,
        AssemblyIdentity assemblyIdentity)
    {
        _logger.LogDebug("{0} requires {1}", metadataReference.Display, assemblyIdentity.GetDisplayName());
        return GetOrAddReference($"{assemblyIdentity.Name}.dll");
    }

    public IEnumerable<PortableExecutableReference> GetFrameworkReferences()
    {
        foreach (string path in Directory.EnumerateFiles(_runtimePath, "*.dll"))
        {
            string name = Path.GetFileName(path);
            if (!IsFramework(Path.GetFileNameWithoutExtension(name)) || !HasMetadata(path))
            {
                continue;
            }

            PortableExecutableReference? reference = GetOrAddReference(name);
            if (reference != null)
            {
                yield return reference;
            }
        }
    }

    public PortableExecutableReference? GetOrAddReference(string name)
    {
        PortableExecutableReference? cachedReference = GetCachedReference(name);
        if (cachedReference != null)
        {
            return cachedReference;
        }

        foreach (string directory in new[] { _appConfiguration.GetDirectoryConfiguration().Libraries, _runtimePath })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                _logger.LogDebug("Creating reference {0} from {1}", name, path);
                return GetMetadataReferenceFromFile(name, path);
            }
        }

        _logger.LogError("Unable to find required dependency {0}", name);
        return null;
    }

    public PortableExecutableReference? GetOrAddReference(CompilerFile compilerFile)
    {
        string name = Path.GetFileName(compilerFile.Name);
        PortableExecutableReference? cachedReference = GetCachedReference(name);
        if (cachedReference != null)
        {
            return cachedReference;
        }

        if (compilerFile.Data is { Length: > 0 })
        {
            _logger.LogDebug("Creating reference from image {0}", name);
            return GetMetadataReferenceFromImage(compilerFile.Data, name);
        }

        if (File.Exists(compilerFile.Name))
        {
            _logger.LogDebug("Creating reference {0} from {1}", name, compilerFile.Name);
            return GetMetadataReferenceFromFile(name, compilerFile.Name);
        }

        return GetOrAddReference(name);
    }

    private static bool IsFramework(string name) =>
        name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or "Microsoft.VisualBasic"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal);

    private static bool HasMetadata(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader reader = new(stream);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private PortableExecutableReference? GetCachedReference(string name)
    {
        AssemblyMetadata? reference = _references.GetValueOrDefault(name);
        return reference?.GetReference(filePath: name);
    }

    private PortableExecutableReference GetMetadataReferenceFromImage(byte[] data, string name)
    {
        AssemblyMetadata assemblyMetadata = AssemblyMetadata.CreateFromImage(data);
        _references.Add(name, assemblyMetadata);
        return assemblyMetadata.GetReference(filePath: name);
    }

    private PortableExecutableReference GetMetadataReferenceFromFile(string name, string path)
    {
        AssemblyMetadata assemblyMetadata = AssemblyMetadata.CreateFromFile(path);
        _references.Add(name, assemblyMetadata);
        return assemblyMetadata.GetReference(filePath: name);
    }

    public void Cleanup()
    {
        _logger.LogDebug("Cleaning up {0} references", _references.Count);
        foreach (KeyValuePair<string, AssemblyMetadata> entry in _references)
        {
            entry.Value.Dispose();
        }

        _references.Clear();
    }
}
