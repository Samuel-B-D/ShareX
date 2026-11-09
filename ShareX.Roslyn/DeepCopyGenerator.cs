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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ShareX.Roslyn
{
    /// <summary>
    /// Generates deep copy code for partial classes marked (directly or through a base class) with
    /// [ShareX.Copying.GenerateDeepCopy]. Replaces the reflection based ObjectExtensions.Copy, which needs the field
    /// metadata of every type at runtime and silently produced shallow copies of framework collections under NativeAOT.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class DeepCopyGenerator : IIncrementalGenerator
    {
        private const string AttributeName = "ShareX.Copying.GenerateDeepCopyAttribute";
        private const string ShallowAttributeName = "ShareX.Copying.ShallowCopyAttribute";
        private const string InterfaceName = "ShareX.Copying.IDeepCopyable";
        private const string Helpers = "global::ShareX.Copying.DeepCopy";
        private const string MembersMethod = "CopyMembersDeep";

        private static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor("SXR001",
            "Deep copyable type must be partial",
            "'{0}' is deep copyable but not partial, so its reference members ({1}) would be shared with the copy. Make it partial.",
            "ShareX.DeepCopy", DiagnosticSeverity.Warning, true);

        private static readonly DiagnosticDescriptor SharedMember = new DiagnosticDescriptor("SXR002",
            "Member is shared by deep copies",
            "'{0}.{1}' of type '{2}' is not deep copyable and will be shared between the original and the copy. Mark '{2}' with [GenerateDeepCopy] or the member with [ShallowCopy].",
            "ShareX.DeepCopy", DiagnosticSeverity.Warning, true);

        private static readonly DiagnosticDescriptor ReadOnlyMember = new DiagnosticDescriptor("SXR003",
            "Read-only member cannot be deep copied",
            "'{0}.{1}' is read-only or init-only and will be shared between the original and the copy. Make it settable or mark it with [ShallowCopy].",
            "ShareX.DeepCopy", DiagnosticSeverity.Warning, true);

        private static readonly HashSet<string> ImmutableTypes = new HashSet<string>
        {
            "System.String", "System.Type", "System.Uri", "System.Version", "System.TimeZoneInfo",
            "System.Globalization.CultureInfo", "System.Text.Encoding", "System.Object"
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<INamedTypeSymbol> types = context.SyntaxProvider
                .CreateSyntaxProvider(
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol)
                .Where(static symbol => symbol != null)
                .Select(static (symbol, _) => symbol!);

            IncrementalValueProvider<(Compilation, ImmutableArray<INamedTypeSymbol>)> input =
                context.CompilationProvider.Combine(types.Collect());

            context.RegisterSourceOutput(input, static (spc, pair) =>
            {
                Compilation compilation = pair.Item1;
                INamedTypeSymbol? attribute = compilation.GetTypeByMetadataName(AttributeName);

                if (attribute == null)
                {
                    return;
                }

                Context ctx = new Context(compilation, attribute);

                foreach (INamedTypeSymbol type in pair.Item2.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
                {
                    if (type.IsStatic || type.IsGenericType || !ctx.IsCopyable(type))
                    {
                        continue;
                    }

                    string? source = Generate(type, ctx, spc);

                    if (source != null)
                    {
                        string hint = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "").Replace('<', '_').Replace('>', '_') + ".DeepCopy.g.cs";
                        spc.AddSource(hint, source);
                    }
                }
            });
        }

        private sealed class Context
        {
            public Compilation Compilation { get; }
            public INamedTypeSymbol Attribute { get; }
            public INamedTypeSymbol? Interface { get; }
            public INamedTypeSymbol? ShallowAttribute { get; }
            public INamedTypeSymbol? Delegate { get; }

            public Context(Compilation compilation, INamedTypeSymbol attribute)
            {
                Compilation = compilation;
                Attribute = attribute;
                Interface = compilation.GetTypeByMetadataName(InterfaceName);
                ShallowAttribute = compilation.GetTypeByMetadataName(ShallowAttributeName);
                Delegate = compilation.GetTypeByMetadataName("System.Delegate");
            }

            /// <summary>The type or one of its base types carries [GenerateDeepCopy] (the attribute is inherited).</summary>
            public bool IsCopyable(INamedTypeSymbol type)
            {
                for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
                {
                    if (current.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, Attribute)))
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool ImplementsInterface(ITypeSymbol type)
            {
                return Interface != null && (SymbolEqualityComparer.Default.Equals(type, Interface) ||
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, Interface)));
            }
        }

        private static bool IsPartial(INamedTypeSymbol type)
        {
            return type.DeclaringSyntaxReferences.Length > 0 && type.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
        }

        /// <summary>True when a base class already declares the generated virtual member copy method.</summary>
        private static bool HasCopyableBase(INamedTypeSymbol type, Context ctx)
        {
            INamedTypeSymbol? baseType = type.BaseType;
            return baseType != null && ctx.IsCopyable(baseType);
        }

        private readonly struct MemberCopy
        {
            public string Statement { get; }

            public MemberCopy(string statement)
            {
                Statement = statement;
            }
        }

        private static string? Generate(INamedTypeSymbol type, Context ctx, SourceProductionContext spc)
        {
            bool hasBase = HasCopyableBase(type, ctx);
            List<MemberCopy> copies = new List<MemberCopy>();
            List<string> problems = new List<string>();

            foreach (ISymbol member in type.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared && member is not IFieldSymbol)
                {
                    continue;
                }

                switch (member)
                {
                    case IFieldSymbol field when field.AssociatedSymbol is IEventSymbol eventSymbol:
                        // Field-like event: the copy must not keep the original's subscribers.
                        copies.Add(new MemberCopy($"copy.{Escape(eventSymbol.Name)} = null;"));
                        break;
                    case IFieldSymbol field when field.AssociatedSymbol is IPropertySymbol property:
                        AddMember(type, property.Name, property.Type, property, property.SetMethod == null || property.SetMethod.IsInitOnly, ctx, spc, copies, problems);
                        break;
                    case IFieldSymbol field when !field.IsConst && !field.IsImplicitlyDeclared:
                        AddMember(type, field.Name, field.Type, field, field.IsReadOnly, ctx, spc, copies, problems);
                        break;
                }
            }

            if (!IsPartial(type))
            {
                if (problems.Count > 0)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NotPartial, type.Locations.FirstOrDefault(), type.Name, string.Join(", ", problems)));
                }

                return null;
            }

            if (hasBase && copies.Count == 0)
            {
                return null;
            }

            string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable disable");
            sb.AppendLine();

            if (!type.ContainingNamespace.IsGlobalNamespace)
            {
                sb.AppendLine("namespace " + type.ContainingNamespace.ToDisplayString());
                sb.AppendLine("{");
            }

            List<INamedTypeSymbol> containers = new List<INamedTypeSymbol>();

            for (INamedTypeSymbol? container = type.ContainingType; container != null; container = container.ContainingType)
            {
                containers.Insert(0, container);
            }

            foreach (INamedTypeSymbol container in containers)
            {
                sb.AppendLine($"partial {(container.TypeKind == TypeKind.Struct ? "struct" : "class")} {container.Name}");
                sb.AppendLine("{");
            }

            sb.AppendLine(hasBase ? $"partial class {type.Name}" : $"partial class {type.Name} : global::{InterfaceName}");
            sb.AppendLine("{");

            if (!hasBase)
            {
                sb.AppendLine($"    object global::{InterfaceName}.DeepCopyObject()");
                sb.AppendLine("    {");
                sb.AppendLine("        object copy = MemberwiseClone();");
                sb.AppendLine($"        {MembersMethod}(copy);");
                sb.AppendLine("        return copy;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    /// <summary>Deep copies the reference members declared by this class into <paramref name=\"target\"/>, a MemberwiseClone of this instance.</summary>");
                sb.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                sb.AppendLine(type.IsSealed ? $"    private void {MembersMethod}(object target)" : $"    protected virtual void {MembersMethod}(object target)");
            }
            else
            {
                sb.AppendLine($"    protected override void {MembersMethod}(object target)");
            }

            sb.AppendLine("    {");

            if (hasBase)
            {
                sb.AppendLine($"        base.{MembersMethod}(target);");
            }

            if (copies.Count > 0)
            {
                sb.AppendLine($"        {typeName} copy = ({typeName})target;");

                foreach (MemberCopy copy in copies)
                {
                    sb.AppendLine("        " + copy.Statement);
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            foreach (INamedTypeSymbol _ in containers)
            {
                sb.AppendLine("}");
            }

            if (!type.ContainingNamespace.IsGlobalNamespace)
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static void AddMember(INamedTypeSymbol owner, string name, ITypeSymbol memberType, ISymbol member, bool readOnly, Context ctx,
            SourceProductionContext spc, List<MemberCopy> copies, List<string> problems)
        {
            if (member.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, ctx.ShallowAttribute)))
            {
                return;
            }

            string source = "this." + Escape(name);
            string? expression = GetCopyExpression(memberType, source, ctx, 0, out ITypeSymbol? sharedType);

            if (sharedType != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(SharedMember, member.Locations.FirstOrDefault(), owner.Name, name, sharedType.ToDisplayString()));
            }

            if (expression == null)
            {
                return;
            }

            if (readOnly)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ReadOnlyMember, member.Locations.FirstOrDefault(), owner.Name, name));
                return;
            }

            problems.Add(name);
            copies.Add(new MemberCopy($"copy.{Escape(name)} = {expression};"));
        }

        /// <summary>
        /// Expression that copies <paramref name="source"/>, or null when the value can be shared (value types, strings,
        /// immutable types). <paramref name="sharedType"/> is set when a mutable reference type has to be shared.
        /// </summary>
        private static string? GetCopyExpression(ITypeSymbol type, string source, Context ctx, int depth, out ITypeSymbol? sharedType)
        {
            sharedType = null;

            if (depth > 4 || type.IsValueType || type.TypeKind == TypeKind.TypeParameter)
            {
                return null;
            }

            if (ImmutableTypes.Contains(type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.None))) ||
                type.SpecialType == SpecialType.System_String || type.SpecialType == SpecialType.System_Object)
            {
                return type.SpecialType == SpecialType.System_Object ? $"{Helpers}.Copy({source})" : null;
            }

            if (type.TypeKind == TypeKind.Delegate)
            {
                return "null";
            }

            string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (type is IArrayTypeSymbol array && array.Rank == 1)
            {
                string elementName = array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                string item = GetCopyExpression(array.ElementType, "item", ctx, depth + 1, out sharedType) ?? "item";
                return $"{Helpers}.ArrayOf<{elementName}>({source}, static item => {item})";
            }

            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                string definition = named.OriginalDefinition.ToDisplayString();
                string arguments = string.Join(", ", named.TypeArguments.Select(a => a.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

                string? helper = definition switch
                {
                    "System.Collections.Generic.List<T>" => "ListOf",
                    "System.Collections.Generic.HashSet<T>" => "HashSetOf",
                    "System.Collections.ObjectModel.ObservableCollection<T>" => "ObservableCollectionOf",
                    _ => null
                };

                if (helper != null)
                {
                    string item = GetCopyExpression(named.TypeArguments[0], "item", ctx, depth + 1, out sharedType) ?? "item";
                    return $"{Helpers}.{helper}<{arguments}>({source}, static item => {item})";
                }

                if (definition == "System.Collections.Generic.Dictionary<TKey, TValue>")
                {
                    string key = GetCopyExpression(named.TypeArguments[0], "key", ctx, depth + 1, out ITypeSymbol? sharedKey) ?? "key";
                    string value = GetCopyExpression(named.TypeArguments[1], "value", ctx, depth + 1, out ITypeSymbol? sharedValue) ?? "value";
                    sharedType = sharedKey ?? sharedValue;
                    return $"{Helpers}.DictionaryOf<{arguments}>({source}, static key => {key}, static value => {value})";
                }
            }

            if (type is INamedTypeSymbol namedType && (ctx.IsCopyable(namedType) || ctx.ImplementsInterface(namedType)))
            {
                return $"{Helpers}.Object<{typeName}>({source})";
            }

            if (type.TypeKind == TypeKind.Interface || type.IsAbstract)
            {
                // Runtime dispatch: the instance may be deep copyable.
                return $"{Helpers}.Copy<{typeName}>({source})";
            }

            sharedType = type;
            return null;
        }

        private static string Escape(string name)
        {
            return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
        }
    }
}
