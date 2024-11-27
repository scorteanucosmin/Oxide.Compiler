using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;
using Oxide.CompilerServices.Models.Configuration;
using Serilog.Events;

namespace Oxide.CompilerServices.Services;

internal class CompilationService : ICompilationService
{
    private readonly ILogger _logger;

    private readonly OxideSettings _settings;

    private readonly MessageBrokerService _messageBrokerService;

    private readonly MetadataReferenceResolver _metadataReferenceResolver;

    private readonly ISerializer _serializer;

    private readonly ImmutableArray<string> _ignoredCodes = ImmutableArray.Create(new[]
    {
        "CS1701"
    });

    public CompilationService(ILogger<CompilationService> logger, OxideSettings settings,
        MessageBrokerService messageBrokerService, MetadataReferenceResolver metadataReferenceResolver,
        ISerializer serializer)
    {
        _logger = logger;
        _settings = settings;
        _messageBrokerService = messageBrokerService;
        _metadataReferenceResolver = metadataReferenceResolver;
        _serializer = serializer;
    }

    public async ValueTask CompileAsync(int id, CompilerData data, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(Constants.CompileEventId, $"Starting compilation of job {id} | Total Plugins: {data.SourceFiles.Length}");
        string details =
            $"Settings[Encoding: {data.Encoding}, CSVersion: {data.CSharpVersion()}, Target: {data.OutputKind()}, Platform: {data.Platform()}, StdLib: {data.StdLib}, Debug: {data.Debug}, Preprocessor: {string.Join(", ", data.Preprocessor)}]";

        if (Program.ApplicationLogLevel.MinimumLevel <= LogEventLevel.Debug)
        {
            if (data.ReferenceFiles.Length > 0)
            {
                details += Environment.NewLine + $"Reference Files:" + Environment.NewLine;
                for (int i = 0; i < data.ReferenceFiles.Length; i++)
                {
                    CompilerFile reference = data.ReferenceFiles[i];
                    if (i > 0)
                    {
                        details += Environment.NewLine;
                    }

                    details += $"  - [{i + 1}] {Path.GetFileName(reference.Name)}({reference.Data.Length})";
                }
            }

            if (data.SourceFiles.Length > 0)
            {
                details += Environment.NewLine + $"Plugin Files:" + Environment.NewLine;

                for (int i = 0; i < data.SourceFiles.Length; i++)
                {
                    CompilerFile plugin = data.SourceFiles[i];
                    if (i > 0)
                    {
                        details += Environment.NewLine;
                    }

                    details += $"  - [{i + 1}] {Path.GetFileName(plugin.Name)}({plugin.Data.Length})";
                }
            }
        }

        _logger.LogDebug(Constants.CompileEventId, details);

        try
        {
            CompilerMessage compilerMessage = new()
            {
                Id = id,
                Type = MessageType.Data
            };

            CompilationResult compilationResult = new();
            CompilerMessage message = await SafeCompileAsync(data, compilerMessage, compilationResult, cancellationToken);

            if (compilationResult.Data.Length > 0)
            {
                _logger.LogInformation(Constants.CompileEventId, $"Successfully compiled {compilationResult.Success}/{data.SourceFiles.Length} plugins for job {id} in {stopwatch.ElapsedMilliseconds}ms");
            }
            else
            {
                _logger.LogError(Constants.CompileEventId, $"Failed to compile job {id} in {stopwatch.ElapsedMilliseconds}ms");
            }

            _messageBrokerService.SendMessage(message);
            _logger.LogDebug(Constants.CompileEventId, $"Pushing job {id} back to parent");
        }
        catch (Exception exception)
        {
            _logger.LogError(Constants.CompileEventId, exception, $"Error while compiling job {id} - {exception.Message}");
            _messageBrokerService.SendMessage(new CompilerMessage
            {
                Id = id,
                Type = MessageType.Error,
                ExtraData = exception
            });

            throw;
        }
    }

    private async ValueTask<CompilerMessage> SafeCompileAsync(CompilerData compilerData, CompilerMessage compilerMessage,
        CompilationResult compilationResult, CancellationToken cancellationToken)
    {
        try
        {
            if (compilerData == null)
            {
                throw new ArgumentNullException(nameof(compilerData), "Missing compile data");
            }

            if (compilerData.SourceFiles == null || compilerData.SourceFiles.Length == 0)
            {
                throw new ArgumentException("No source files provided", nameof(compilerData.SourceFiles));
            }

            Dictionary<string, MetadataReference> references = new(StringComparer.OrdinalIgnoreCase);

            OxideResolver resolver = (OxideResolver)_metadataReferenceResolver;
            if (compilerData.StdLib)
            {
                references.Add("System.Private.CoreLib.dll", resolver.Reference("System.Private.CoreLib.dll")!);
                references.Add("netstandard.dll", resolver.Reference("netstandard.dll")!);
                references.Add("System.Runtime.dll", resolver.Reference("System.Runtime.dll")!);
                references.Add("System.Collections.dll", resolver.Reference("System.Collections.dll")!);
                references.Add("System.Collections.Immutable.dll",
                    resolver.Reference("System.Collections.Immutable.dll")!);
                references.Add("System.Linq.dll", resolver.Reference("System.Linq.dll")!);
                references.Add("System.Data.Common.dll", resolver.Reference("System.Data.Common.dll")!);
            }

            if (compilerData.ReferenceFiles is { Length: > 0 })
            {
                foreach (CompilerFile referenceFile in compilerData.ReferenceFiles)
                {
                    string fileName = Path.GetFileName(referenceFile.Name);
                    switch (Path.GetExtension(referenceFile.Name))
                    {
                        case ".cs":
                        case ".exe":
                        case ".dll":
                        {
                            references[fileName] = File.Exists(referenceFile.Name) && (referenceFile.Data == null ||
                                referenceFile.Data.Length == 0)
                                ? MetadataReference.CreateFromFile(referenceFile.Name)
                                : MetadataReference.CreateFromImage(referenceFile.Data, filePath: referenceFile.Name);
                            continue;
                        }
                        default:
                        {
                            _logger.LogWarning(Constants.CompileEventId, "Ignoring unhandled project reference: {ref}",
                                fileName);
                            continue;
                        }
                    }
                }

                _logger.LogDebug(Constants.CompileEventId, $"Added {references.Count} project references");
            }

            Dictionary<CompilerFile, SyntaxTree> syntaxTrees = new();
            Encoding encoding = Encoding.GetEncoding(compilerData.Encoding);

            CSharpParseOptions parseOptions = new(compilerData.CSharpVersion(), preprocessorSymbols: compilerData.Preprocessor);

            foreach (CompilerFile compilerFile in compilerData.SourceFiles)
            {
                string fileName = Path.GetFileName(compilerFile.Name);
                bool isUnicode = false;

                string sourceString = RegexExtensions.UnicodeEscapePattern().Replace(
                    encoding.GetString(compilerFile.Data), match =>
                    {
                        isUnicode = true;
                        return ((char)int.Parse(match.Value.Substring(2), NumberStyles.HexNumber)).ToString();
                    });

                if (isUnicode)
                {
                    _logger.LogDebug(Constants.CompileEventId,
                        $"Plugin {fileName} is using unicode escape sequence");
                }

                SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceString, parseOptions, fileName, encoding,
                    cancellationToken);

                syntaxTrees.Add(compilerFile, syntaxTree);
            }

            _logger.LogDebug(Constants.CompileEventId, $"Added {syntaxTrees.Count} plugins to the project");

            CSharpCompilationOptions compilationOptions = new(compilerData.OutputKind(), metadataReferenceResolver: resolver,
                platform: compilerData.Platform(),
                allowUnsafe: true,
                optimizationLevel: compilerData.Debug ? OptimizationLevel.Debug : OptimizationLevel.Release);

            string assemblyName = Path.GetRandomFileName();
            CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, syntaxTrees.Values,
                references.Values, compilationOptions);

            compilationResult.Name = compilation.AssemblyName;

            CompileProject(compilation, compilerData, compilerMessage, compilationResult, cancellationToken);

            compilerMessage.Data = _serializer.Serialize(compilationResult);
            return compilerMessage;
        }
        catch (Exception exception)
        {
            _logger.LogError("Error while compiling: {0}", exception);
            throw;
        }
    }

    private void CompileProject(CSharpCompilation compilation, CompilerData compilerData, CompilerMessage compilerMessage,
        CompilationResult compilationResult, CancellationToken cancellationToken)
    {
        using MemoryStream peStream = new();

        EmitResult result = compilation.Emit(peStream, options: compilerData.Debug ? Constants.PdbEmitOptions : null,
            cancellationToken: cancellationToken);

        if (result.Success)
        {
            compilationResult.Data = peStream.ToArray();
            compilationResult.Success = compilation.SyntaxTrees.Length;
            return;
        }

        bool modified = false;

        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            if (_ignoredCodes.Contains(diagnostic.Id))
            {
                continue;
            }

            if (diagnostic.Location.SourceTree != null)
            {
                SyntaxTree tree = diagnostic.Location.SourceTree;
                LocationKind kind = diagnostic.Location.Kind;
                string? fileName = tree.FilePath ?? "UnknownFile.cs";
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                int line = span.StartLinePosition.Line + 1;
                int charPos = span.StartLinePosition.Character + 1;

                if (compilation.SyntaxTrees.Contains(tree) && diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    _logger.LogWarning(Constants.CompileEventId, "Failed to compile {tree} - {message} (L: {line} | P: {pos}) | Removing from project",
                        fileName, diagnostic.GetMessage(), line, charPos);

                    compilation = compilation.RemoveSyntaxTrees(tree);
                    compilerMessage.ExtraData += $"[Error][{diagnostic.Id}][{fileName}] {diagnostic.GetMessage()} | Line: {line}, Pos: {charPos} {Environment.NewLine}";
                    modified = true;
                    compilationResult.Failed++;
                }
            }
            else
            {
                _logger.LogError(Constants.CompileEventId, $"[Error][{diagnostic.Id}] {diagnostic.GetMessage()}");
            }
        }

        if (modified && compilation.SyntaxTrees.Length > 0)
        {
            CompileProject(compilation, compilerData, compilerMessage, compilationResult, cancellationToken);
        }
    }
}
