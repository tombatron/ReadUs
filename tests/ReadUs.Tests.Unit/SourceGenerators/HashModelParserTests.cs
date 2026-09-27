using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ReadUs.SourceGenerators.HashModel;

namespace ReadUs.Tests.Unit.SourceGenerators;

/// <summary>
/// Unit-level coverage of <see cref="HashModelParser"/>'s classification and diagnostic
/// rules (project spec §10) using small, self-contained source snippets compiled
/// in-process — end-to-end coverage (the generator actually running, the emitted code
/// actually compiling and round-tripping through a real server) lives in
/// ReadUs.Tests.Integration.HashRedisClientExtensionsTests.
/// </summary>
public class HashModelParserTests
{
    [Fact]
    public void ParsesAPlainMutablePocoWithAllSupportedScalarTypes()
    {
        var type = Compile("""
            public partial class Widget
            {
                public string Name { get; set; } = "";
                public int Count { get; set; }
                public long BigCount { get; set; }
                public double Weight { get; set; }
                public bool Enabled { get; set; }
                public System.Guid Id { get; set; }
                public System.DateTime CreatedAt { get; set; }
                public Color Favorite { get; set; }
                public byte[] Blob { get; set; } = System.Array.Empty<byte>();
                public string? Nickname { get; set; }
                public int? OptionalCount { get; set; }
            }

            public enum Color { Red, Blue }
            """);

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Empty(diagnostics);
        Assert.NotNull(definition);
        Assert.Equal("class", definition!.TypeKeyword);
        Assert.Equal(HashModelConstructionStrategy.ParameterlessThenInitializer, definition.ConstructionStrategy);
        Assert.Equal(11, definition.Properties.Count);

        var byName = definition.Properties.ToDictionary(p => p.Name);
        Assert.Equal(HashFieldKind.String, byName["Name"].Kind);
        Assert.False(byName["Name"].IsNullable);
        Assert.Equal(HashFieldKind.Int32, byName["Count"].Kind);
        Assert.Equal(HashFieldKind.Int64, byName["BigCount"].Kind);
        Assert.Equal(HashFieldKind.Double, byName["Weight"].Kind);
        Assert.Equal(HashFieldKind.Boolean, byName["Enabled"].Kind);
        Assert.Equal(HashFieldKind.Guid, byName["Id"].Kind);
        Assert.Equal(HashFieldKind.DateTime, byName["CreatedAt"].Kind);
        Assert.Equal(HashFieldKind.Enum, byName["Favorite"].Kind);
        Assert.Equal(HashFieldKind.Bytes, byName["Blob"].Kind);
        Assert.True(byName["Nickname"].IsNullable);
        Assert.True(byName["OptionalCount"].IsNullable);
    }

    [Fact]
    public void ParsesAPositionalRecordViaItsPrimaryConstructor()
    {
        var type = Compile("public partial record Point(int X, int Y);");

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Empty(diagnostics);
        Assert.NotNull(definition);
        Assert.Equal("record", definition!.TypeKeyword);
        Assert.Equal(HashModelConstructionStrategy.Positional, definition.ConstructionStrategy);
        Assert.Equal(["X", "Y"], definition.ConstructorParameterOrder);
    }

    [Fact]
    public void IgnoresAPropertyMarkedRedisHashIgnore()
    {
        var type = Compile("""
            public partial class Widget
            {
                public string Name { get; set; } = "";

                [ReadUs.Extensions.Hashes.RedisHashIgnore]
                public string Secret { get; set; } = "";
            }
            """);

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Empty(diagnostics);
        Assert.NotNull(definition);
        Assert.Single(definition!.Properties);
        Assert.Equal("Name", definition.Properties[0].Name);
    }

    [Fact]
    public void ReportsADiagnosticWhenTheTypeIsNotPartial()
    {
        var type = Compile("public class Widget { public string Name { get; set; } = \"\"; }");

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Null(definition);
        Assert.Equal(HashModelParser.NotPartial.Id, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void ReportsADiagnosticForANestedType()
    {
        var type = Compile(
            "public partial class Outer { public partial class Widget { public string Name { get; set; } = \"\"; } }",
            "Widget");

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Null(definition);
        Assert.Equal(HashModelParser.NestedTypeNotSupported.Id, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void ReportsADiagnosticForAnUnsupportedPropertyType()
    {
        var type = Compile("""
            public partial class Widget
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<int> Tags { get; set; } = new();
            }
            """);

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Null(definition);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(HashModelParser.UnsupportedPropertyType.Id, diagnostic.Id);
        Assert.Contains("Tags", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsADiagnosticWhenNoConstructorMatchesTheMappedProperties()
    {
        var type = Compile("""
            public partial class Widget
            {
                public Widget(string onlySomeOfIt) => Name = onlySomeOfIt;
                public string Name { get; set; }
                public int Count { get; set; }
            }
            """);

        var (definition, diagnostics) = HashModelParser.Parse(type);

        Assert.Null(definition);
        Assert.Equal(HashModelParser.NoUsableConstructor.Id, Assert.Single(diagnostics).Id);
    }

    private static INamedTypeSymbol Compile(string source, string? typeName = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(ReadUs.Extensions.Hashes.RedisHashIgnoreAttribute).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "HashModelParserTests.Compilation",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var root = syntaxTree.GetRoot();
        var typeDeclaration = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(t => typeName is null || t.Identifier.Text == typeName);

        var model = compilation.GetSemanticModel(syntaxTree);
        return (INamedTypeSymbol)model.GetDeclaredSymbol(typeDeclaration)!;
    }
}
