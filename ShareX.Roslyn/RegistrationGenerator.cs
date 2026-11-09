#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ShareX.Roslyn
{
    /// <summary>
    /// Generates ShareX.Generated.ShareXGeneratedRegistration_{Assembly}.Register(): registers the source generated
    /// System.Text.Json contexts of the assembly with ShareXJson and a typed, lenient enum converter for the enums ShareX serializes.
    /// <para>
    /// ShareX settings must keep loading when an enum member was renamed or removed (Newtonsoft.Json used to skip the
    /// value), and old files store enums by name. That needs a JsonConverter&lt;TEnum&gt; per enum, and NativeAOT cannot
    /// create closed generic converters over value types at runtime with MakeGenericType, so every assembly that
    /// references ShareX.HelpersLib registers, from a module initializer:
    /// </para>
    /// <list type="bullet">
    /// <item>the enums it declares,</item>
    /// <item>the enums used by fields and properties of its types (SkiaSharp, BCL, ... enums inside settings),</item>
    /// <item>the public enums of referenced ShareX assemblies that cannot register themselves (they do not reference ShareX.HelpersLib).</item>
    /// </list>
    /// Registering the same enum from several assemblies is harmless.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class RegistrationGenerator : IIncrementalGenerator
    {
        private const string FactoryTypeName = "ShareX.HelpersLib.Json.LenientEnumConverterFactory";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValueProvider<RegistrationModel?> model = context.CompilationProvider
                .Select(static (compilation, cancellationToken) =>
                {
                    if (compilation.GetTypeByMetadataName(FactoryTypeName) == null)
                    {
                        return null;
                    }

                    HashSet<INamedTypeSymbol> found = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                    // Types of this assembly: declared enums (internal allowed) and enums used by members.
                    foreach (INamedTypeSymbol type in GetTypes(compilation.Assembly.GlobalNamespace))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Collect(type, found, compilation.Assembly, includeMembers: true);
                    }

                    // Referenced ShareX assemblies: their public enums and the enums used by their public members.
                    foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
                    {
                        if (assembly.Name == "ShareX" || assembly.Name.StartsWith("ShareX.", System.StringComparison.Ordinal))
                        {
                            foreach (INamedTypeSymbol type in GetTypes(assembly.GlobalNamespace))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                Collect(type, found, compilation.Assembly, includeMembers: true);
                            }
                        }
                    }

                    ImmutableArray<string> names = found
                        .Where(symbol => IsAccessible(symbol, compilation.Assembly))
                        .Select(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .Distinct()
                        .OrderBy(name => name, System.StringComparer.Ordinal)
                        .ToImmutableArray();

                    // Source generated System.Text.Json contexts declared in this assembly.
                    INamedTypeSymbol? contextBase = compilation.GetTypeByMetadataName(JsonSerializerContextTypeName);
                    ImmutableArray<string> contexts = contextBase == null ? ImmutableArray<string>.Empty : GetTypes(compilation.Assembly.GlobalNamespace)
                        .Where(type => !type.IsAbstract && !type.IsGenericType && SymbolEqualityComparer.Default.Equals(type.BaseType, contextBase) &&
                            IsAccessible(type, compilation.Assembly))
                        .Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .OrderBy(name => name, System.StringComparer.Ordinal)
                        .ToImmutableArray();

                    // Registration classes generated in referenced ShareX assemblies.
                    ImmutableArray<string> references = compilation.SourceModule.ReferencedAssemblySymbols
                        .Where(assembly => assembly.Name == "ShareX" || assembly.Name.StartsWith("ShareX.", System.StringComparison.Ordinal))
                        .Select(assembly => assembly.GetTypeByMetadataName(GeneratedNamespace + "." + GetRegistrationClassName(assembly.Name)))
                        .Where(type => type != null && type.DeclaredAccessibility == Accessibility.Public)
                        .Select(type => type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        .OrderBy(name => name, System.StringComparer.Ordinal)
                        .ToImmutableArray();

                    return new RegistrationModel(compilation.AssemblyName ?? "Assembly", names, contexts, references);
                });

            context.RegisterSourceOutput(model, static (spc, input) =>
            {
                if (input != null)
                {
                    spc.AddSource("ShareXGeneratedRegistration.g.cs", Generate(input));
                }
            });
        }

        private static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceSymbol ns)
        {
            foreach (INamespaceOrTypeSymbol member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    foreach (INamedTypeSymbol type in GetTypes(childNamespace)) yield return type;
                }
                else if (member is INamedTypeSymbol type)
                {
                    foreach (INamedTypeSymbol nested in GetTypeAndNested(type)) yield return nested;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetTypeAndNested(INamedTypeSymbol type)
        {
            yield return type;

            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            {
                foreach (INamedTypeSymbol inner in GetTypeAndNested(nested)) yield return inner;
            }
        }

        private static void Collect(INamedTypeSymbol type, HashSet<INamedTypeSymbol> found, IAssemblySymbol target, bool includeMembers)
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                found.Add(type);
                return;
            }

            if (!includeMembers || type.IsGenericType && type.IsUnboundGenericType)
            {
                return;
            }

            foreach (ISymbol member in type.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared)
                {
                    continue;
                }

                ITypeSymbol? memberType = member switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property when !property.IsIndexer => property.Type,
                    _ => null
                };

                if (memberType != null)
                {
                    AddEnums(memberType, found, 0);
                }
            }
        }

        /// <summary>Finds enums in a member type, including Nullable&lt;T&gt;, arrays and generic arguments (List&lt;T&gt;, Dictionary&lt;K,V&gt;).</summary>
        private static void AddEnums(ITypeSymbol type, HashSet<INamedTypeSymbol> found, int depth)
        {
            if (depth > 4) return;

            switch (type)
            {
                case IArrayTypeSymbol array:
                    AddEnums(array.ElementType, found, depth + 1);
                    break;
                case INamedTypeSymbol named when named.TypeKind == TypeKind.Enum:
                    found.Add(named);
                    break;
                case INamedTypeSymbol named when named.IsGenericType:
                    foreach (ITypeSymbol argument in named.TypeArguments) AddEnums(argument, found, depth + 1);
                    break;
            }
        }

        private static bool IsAccessible(INamedTypeSymbol symbol, IAssemblySymbol target)
        {
            for (INamedTypeSymbol? current = symbol; current != null; current = current.ContainingType)
            {
                if (current.IsGenericType)
                {
                    return false;
                }

                switch (current.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                        if (!SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, target)) return false;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        private const string JsonSerializerContextTypeName = "System.Text.Json.Serialization.JsonSerializerContext";
        private const string ShareXJsonTypeName = "ShareX.HelpersLib.Json.ShareXJson";
        private const string GeneratedNamespace = "ShareX.Generated";

        private sealed class RegistrationModel : System.IEquatable<RegistrationModel>
        {
            public string AssemblyName { get; }
            public ImmutableArray<string> Enums { get; }
            public ImmutableArray<string> Contexts { get; }
            public ImmutableArray<string> References { get; }

            public RegistrationModel(string assemblyName, ImmutableArray<string> enums, ImmutableArray<string> contexts, ImmutableArray<string> references)
            {
                AssemblyName = assemblyName;
                Enums = enums;
                Contexts = contexts;
                References = references;
            }

            public bool Equals(RegistrationModel? other) => other != null && AssemblyName == other.AssemblyName &&
                Enums.SequenceEqual(other.Enums) && Contexts.SequenceEqual(other.Contexts) && References.SequenceEqual(other.References);

            public override bool Equals(object? obj) => Equals(obj as RegistrationModel);

            public override int GetHashCode() => AssemblyName.GetHashCode() ^ Enums.Length ^ (Contexts.Length << 8) ^ (References.Length << 16);
        }

        public static string GetRegistrationClassName(string assemblyName)
        {
            return "ShareXGeneratedRegistration_" + new string(assemblyName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        }

        /// <summary>
        /// One public Register() per assembly: it registers the referenced ShareX assemblies first, then the JSON contexts
        /// and enum converters of this assembly. Module initializers of libraries are not guaranteed to run under NativeAOT,
        /// so the module initializer of the entry assembly (which does run) reaches every assembly through these calls.
        /// </summary>
        private static string Generate(RegistrationModel model)
        {
            string className = GetRegistrationClassName(model.AssemblyName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("namespace " + GeneratedNamespace);
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Registers the System.Text.Json contexts and lenient enum converters of " + model.AssemblyName + " with ShareXJson.</summary>");
            sb.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            sb.AppendLine("    public static class " + className);
            sb.AppendLine("    {");
            sb.AppendLine("        private static int registered;");
            sb.AppendLine();
            sb.AppendLine("#pragma warning disable CA2255 // Registration must run before any JSON is (de)serialized.");
            sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
            sb.AppendLine("#pragma warning restore CA2255");
            sb.AppendLine("        internal static void Initialize() => Register();");
            sb.AppendLine();
            sb.AppendLine("        public static void Register()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (global::System.Threading.Interlocked.Exchange(ref registered, 1) != 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine();

            foreach (string reference in model.References)
            {
                sb.AppendLine($"            {reference}.Register();");
            }

            foreach (string context in model.Contexts)
            {
                sb.AppendLine($"            global::{ShareXJsonTypeName}.AddResolver({context}.Default);");
            }

            foreach (string name in model.Enums)
            {
                sb.AppendLine($"            global::{FactoryTypeName}.Register<{name}>();");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
