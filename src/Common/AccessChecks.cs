using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Oxide.CompilerServices.Common;

public static class AccessChecks
{
    private const string AttributeName = "IgnoresAccessChecksToAttribute";

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(CSharpCompilationOptions))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, "Microsoft.CodeAnalysis.CSharp.BinderFlags", "Microsoft.CodeAnalysis.CSharp")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Kept by the DynamicDependency attributes")]
    public static CSharpCompilationOptions Ignore(CSharpCompilationOptions options)
    {
        options = options.WithMetadataImportOptions(MetadataImportOptions.All);

        PropertyInfo? flags = typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
        if (flags == null)
        {
            throw new MissingMemberException(nameof(CSharpCompilationOptions), "TopLevelBinderFlags");
        }

        flags.SetValue(options, Enum.Parse(flags.PropertyType, "IgnoreAccessibility"));
        return options;
    }

    public static SyntaxTree Attributes(IEnumerable<string> assemblyNames, CSharpParseOptions parseOptions, IEnumerable<SyntaxTree> sources)
    {
        StringBuilder source = new();
        foreach (string assemblyName in assemblyNames)
        {
            source.Append("[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"").Append(assemblyName.Replace("\"", "\\\"")).AppendLine("\")]");
        }

        if (!sources.Any(tree => tree.GetText().ToString().Contains(AttributeName, StringComparison.Ordinal)))
        {
            source.AppendLine($$"""
                namespace System.Runtime.CompilerServices
                {
                    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                    internal sealed class {{AttributeName}} : Attribute
                    {
                        public {{AttributeName}}(string assemblyName) => AssemblyName = assemblyName;

                        public string AssemblyName { get; }
                    }
                }
                """);
        }

        return CSharpSyntaxTree.ParseText(source.ToString(), parseOptions, path: string.Empty, encoding: Encoding.UTF8);
    }
}
