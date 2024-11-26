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

    private readonly CancellationToken _cancellationToken;

    private readonly ImmutableArray<string> _ignoredCodes = ImmutableArray.Create(new[]
    {
        "CS1701"
    });

    public CompilationService(ILogger<CompilationService> logger, OxideSettings settings, MessageBrokerService messageBrokerService,
        MetadataReferenceResolver metadataReferenceResolver, ISerializer serializer, CancellationTokenSource cancellationTokenSource)
    {
        _logger = logger;
        _settings = settings;
        _messageBrokerService = messageBrokerService;
        _metadataReferenceResolver = metadataReferenceResolver;
        _serializer = serializer;
        _cancellationToken = cancellationTokenSource.Token;
    }

    public async Task Compile(int id, CompilerData data)
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
            CompilerMessage message = await SafeCompile(data, compilerMessage, compilationResult);

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

    private async Task<CompilerMessage> SafeCompile(CompilerData data, CompilerMessage compilerMessage,
        CompilationResult compilationResult)
    {
        try
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data), "Missing compile data");
            }

            if (data.SourceFiles == null || data.SourceFiles.Length == 0)
            {
                throw new ArgumentException("No source files provided", nameof(data.SourceFiles));
            }

            OxideResolver resolver = (OxideResolver)_metadataReferenceResolver;

            Dictionary<string, MetadataReference> references = new(StringComparer.OrdinalIgnoreCase);

            if (data.StdLib)
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

            if (data.ReferenceFiles is { Length: > 0 })
            {
                foreach (CompilerFile reference in data.ReferenceFiles)
                {
                    string fileName = Path.GetFileName(reference.Name);
                    switch (Path.GetExtension(reference.Name))
                    {
                        case ".cs":
                        case ".exe":
                        case ".dll":
                            references[fileName] = File.Exists(reference.Name) && (reference.Data == null ||
                                reference.Data.Length == 0)
                                ? MetadataReference.CreateFromFile(reference.Name)
                                : MetadataReference.CreateFromImage(reference.Data, filePath: reference.Name);
                            continue;

                        default:
                            _logger.LogWarning(Constants.CompileEventId, "Ignoring unhandled project reference: {ref}",
                                fileName);
                            continue;
                    }
                }

                _logger.LogDebug(Constants.CompileEventId, $"Added {references.Count} project references");
            }

            Dictionary<CompilerFile, SyntaxTree> trees = new();
            Encoding encoding = Encoding.GetEncoding(data.Encoding);
            CSharpParseOptions options = new(data.CSharpVersion(), preprocessorSymbols: data.Preprocessor);
            foreach (CompilerFile source in data.SourceFiles)
            {
                string fileName = Path.GetFileName(source.Name);
                bool isUnicode = false;

                string sourceString = RegexExtensions.UnicodeEscapePattern().Replace(
                    encoding.GetString(source.Data), match =>
                    {
                        isUnicode = true;
                        return ((char)int.Parse(match.Value.Substring(2), NumberStyles.HexNumber)).ToString();
                    });

                if (isUnicode)
                {
                    _logger.LogDebug(Constants.CompileEventId, $"Plugin {fileName} is using unicode escape sequence");
                }

                SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceString, options, path: fileName,
                    encoding: encoding, cancellationToken: _cancellationToken);
                trees.Add(source, tree);
            }

            _logger.LogDebug(Constants.CompileEventId, $"Added {trees.Count} plugins to the project");

            CSharpCompilationOptions compOptions = new(data.OutputKind(), metadataReferenceResolver: resolver,
                platform: data.Platform(),
                allowUnsafe: true,
                optimizationLevel: data.Debug ? OptimizationLevel.Debug : OptimizationLevel.Release);

            CSharpCompilation comp = CSharpCompilation.Create(Path.GetRandomFileName(), trees.Values,
                references.Values, compOptions);

            compilationResult.Name = comp.AssemblyName;

            CompileProject(comp, compilerMessage, compilationResult);

            compilerMessage.Data = _serializer.Serialize(compilationResult);
            return compilerMessage;
        }
        catch (Exception exception)
        {
            _logger.LogError("Error while compiling: {0}", exception);
            throw;
        }
    }

    private void CompileProject(CSharpCompilation compilation, CompilerMessage message, CompilationResult compilationResult)
    {
        using MemoryStream pe = new();
        EmitResult result = compilation.Emit(pe, cancellationToken: _cancellationToken);

        if (result.Success)
        {
            compilationResult.Data = pe.ToArray();
            compilationResult.Success = compilation.SyntaxTrees.Length;
            return;
        }

        bool modified = false;

        foreach (Diagnostic diag in result.Diagnostics)
        {
            if (_ignoredCodes.Contains(diag.Id))
            {
                continue;
            }

            if (diag.Location.SourceTree != null)
            {
                SyntaxTree tree = diag.Location.SourceTree;
                LocationKind kind = diag.Location.Kind;
                string? fileName = tree.FilePath ?? "UnknownFile.cs";
                FileLinePositionSpan span = diag.Location.GetLineSpan();
                int line = span.StartLinePosition.Line + 1;
                int charPos = span.StartLinePosition.Character + 1;

                if (compilation.SyntaxTrees.Contains(tree) && diag.Severity == DiagnosticSeverity.Error)
                {
                    _logger.LogWarning(Constants.CompileEventId, "Failed to compile {tree} - {message} (L: {line} | P: {pos}) | Removing from project", fileName, diag.GetMessage(), line, charPos);
                    compilation = compilation.RemoveSyntaxTrees(tree);
                    message.ExtraData += $"[Error][{diag.Id}][{fileName}] {diag.GetMessage()} | Line: {line}, Pos: {charPos} {Environment.NewLine}";
                    modified = true;
                    compilationResult.Failed++;
                }
            }
            else
            {
                _logger.LogError(Constants.CompileEventId, $"[Error][{diag.Id}] {diag.GetMessage()}");
            }
        }

        if (modified && compilation.SyntaxTrees.Length > 0)
        {
            CompileProject(compilation, message, compilationResult);
        }
    }
}
