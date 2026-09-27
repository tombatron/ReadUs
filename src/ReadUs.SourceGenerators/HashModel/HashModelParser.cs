using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReadUs.SourceGenerators.HashModel;

internal static class HashModelParser
{
    public static readonly DiagnosticDescriptor NotPartial = new(
        "READUSHASH001",
        "RedisHashModel type must be declared partial",
        "'{0}' is decorated with [RedisHashModel] but isn't declared partial — the generator adds mapping members via a second partial declaration",
        "ReadUs.Hashes",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedTypeNotSupported = new(
        "READUSHASH002",
        "RedisHashModel type must not be nested",
        "'{0}' is a nested type — [RedisHashModel] only supports top-level types",
        "ReadUs.Hashes",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        "READUSHASH003",
        "Unsupported RedisHashModel property type",
        "Property '{0}' on '{1}' has type '{2}', which isn't a supported hash field type (string, byte[], int, long, double, bool, Guid, DateTime, an enum, or a nullable of one of these value types)",
        "ReadUs.Hashes",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoUsableConstructor = new(
        "READUSHASH004",
        "RedisHashModel type has no usable constructor",
        "'{0}' needs either a public parameterless constructor, or a public constructor whose parameters exactly match every mapped property by name",
        "ReadUs.Hashes",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static (HashModelDefinition? Definition, ImmutableArray<Diagnostic> Diagnostics) Parse(INamedTypeSymbol typeSymbol)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (typeSymbol.ContainingType is not null)
        {
            diagnostics.Add(Diagnostic.Create(NestedTypeNotSupported, typeSymbol.Locations.FirstOrDefault(), typeSymbol.Name));
            return (null, diagnostics.ToImmutable());
        }

        if (typeSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not TypeDeclarationSyntax declaration || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(Diagnostic.Create(NotPartial, typeSymbol.Locations.FirstOrDefault(), typeSymbol.Name));
            return (null, diagnostics.ToImmutable());
        }

        var typeKeyword = TypeKeyword(typeSymbol, declaration);

        var properties = new List<HashModelProperty>();
        foreach (var property in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.DeclaredAccessibility != Accessibility.Public
                || property.IsStatic
                || property.GetMethod is null
                || property.SetMethod is null
                || property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "ReadUs.Extensions.Hashes.RedisHashIgnoreAttribute"))
            {
                continue;
            }

            var classified = Classify(property.Type);
            if (classified is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedPropertyType,
                    property.Locations.FirstOrDefault(),
                    property.Name,
                    typeSymbol.Name,
                    property.Type.ToDisplayString()));
                continue;
            }

            properties.Add(WithName(classified, property.Name));
        }

        if (diagnostics.Count > 0)
        {
            return (null, diagnostics.ToImmutable());
        }

        var (strategy, order) = ResolveConstructionStrategy(typeSymbol, properties);
        if (strategy is null)
        {
            diagnostics.Add(Diagnostic.Create(NoUsableConstructor, typeSymbol.Locations.FirstOrDefault(), typeSymbol.Name));
            return (null, diagnostics.ToImmutable());
        }

        var definition = new HashModelDefinition
        {
            Namespace = typeSymbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : typeSymbol.ContainingNamespace.ToDisplayString(),
            TypeKeyword = typeKeyword,
            TypeName = typeSymbol.Name,
            ConstructionStrategy = strategy.Value,
            ConstructorParameterOrder = order,
            Properties = properties,
        };

        return (definition, diagnostics.ToImmutable());
    }

    private static HashModelProperty WithName(HashModelProperty property, string name) => new()
    {
        Name = name,
        Kind = property.Kind,
        IsNullable = property.IsNullable,
        EnumTypeName = property.EnumTypeName,
    };

    private static string TypeKeyword(INamedTypeSymbol typeSymbol, TypeDeclarationSyntax declaration)
    {
        var isRecord = declaration is RecordDeclarationSyntax;
        return (isRecord, typeSymbol.TypeKind) switch
        {
            (true, Microsoft.CodeAnalysis.TypeKind.Struct) => "record struct",
            (true, _) => "record",
            (false, Microsoft.CodeAnalysis.TypeKind.Struct) => "struct",
            _ => "class",
        };
    }

    private static HashModelProperty? Classify(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            var inner = Classify(nullable.TypeArguments[0]);
            return inner is null ? null : new HashModelProperty { Name = string.Empty, Kind = inner.Kind, IsNullable = true, EnumTypeName = inner.EnumTypeName };
        }

        var isNullableRef = type.NullableAnnotation == NullableAnnotation.Annotated;

        if (type.SpecialType == SpecialType.System_String)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.String, IsNullable = isNullableRef };
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Bytes, IsNullable = isNullableRef };
        }

        if (type.SpecialType == SpecialType.System_Int32)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Int32, IsNullable = false };
        }

        if (type.SpecialType == SpecialType.System_Int64)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Int64, IsNullable = false };
        }

        if (type.SpecialType == SpecialType.System_Double)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Double, IsNullable = false };
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Boolean, IsNullable = false };
        }

        if (type.ToDisplayString() == "System.Guid")
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Guid, IsNullable = false };
        }

        if (type.ToDisplayString() == "System.DateTime")
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.DateTime, IsNullable = false };
        }

        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
        {
            return new HashModelProperty { Name = string.Empty, Kind = HashFieldKind.Enum, IsNullable = false, EnumTypeName = type.ToDisplayString() };
        }

        return null;
    }

    private static (HashModelConstructionStrategy? Strategy, IReadOnlyList<string> Order) ResolveConstructionStrategy(INamedTypeSymbol typeSymbol, List<HashModelProperty> properties)
    {
        var constructors = typeSymbol.Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic).ToList();

        if (constructors.Any(c => c.Parameters.Length == 0))
        {
            return (HashModelConstructionStrategy.ParameterlessThenInitializer, []);
        }

        var propertyNames = new HashSet<string>(properties.Select(p => p.Name));
        foreach (var ctor in constructors)
        {
            if (ctor.Parameters.Length != properties.Count)
            {
                continue;
            }

            var parameterNames = ctor.Parameters.Select(p => p.Name).ToList();
            if (parameterNames.All(propertyNames.Contains) && parameterNames.Distinct().Count() == parameterNames.Count)
            {
                return (HashModelConstructionStrategy.Positional, parameterNames);
            }
        }

        return (null, []);
    }
}
