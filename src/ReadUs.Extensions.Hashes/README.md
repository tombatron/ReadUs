# ReadUs.Extensions.Hashes

Zero-reflection Redis Hash mapping for [ReadUs](https://github.com/tombatron/ReadUs):
a Roslyn incremental source generator maps `[RedisHashModel]`-decorated
POCOs directly to/from `HSET`/`HGETALL` fields at compile time — no
`Activator.CreateInstance`, no `PropertyInfo`, nothing reflection-based
anywhere in the generated path.

## Install

Not yet published to NuGet — reference the project directly. Because the
mapping is generated for *your own* `[RedisHashModel]` types, your project
also needs a direct analyzer reference to `ReadUs.SourceGenerators` —
referencing this package alone isn't enough:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/ReadUs/src/ReadUs.Extensions.Hashes/ReadUs.Extensions.Hashes.csproj" />
  <ProjectReference Include="path/to/ReadUs/src/ReadUs.SourceGenerators/ReadUs.SourceGenerators.csproj"
                     OutputItemType="Analyzer"
                     ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Example

```csharp
using ReadUs.Extensions.Hashes;

[RedisHashModel]
public partial class Account
{
    public string Name { get; set; } = "";
    public int Balance { get; set; }
}

await client.SetHashAsync("account:1"u8.ToArray(), new Account { Name = "Alice", Balance = 100 });
var account = await client.GetHashAsync<Account>("account:1"u8.ToArray());
```

## Documentation

See the [Hash and JSON mapping guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/hash-and-json-mapping.md)
and the [root README](https://github.com/tombatron/ReadUs).
