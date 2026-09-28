# Hash and JSON mapping

Two separate, optional convenience packages, covering two different
shapes of "map a POCO to Redis" — pick whichever matches how the data
actually needs to be queried. Both are **zero-reflection**: this is a hard
bar for this project (see [`CONTRIBUTING.md`](../../CONTRIBUTING.md)), not
just the core command path.

## `ReadUs.Extensions.Hashes` — per-property Hash mapping

A Roslyn incremental source generator maps a `[RedisHashModel]`-decorated
POCO directly to/from `HSET`/`HGETALL` fields at **compile time** — no
`Activator.CreateInstance`, no `PropertyInfo`, nothing reflection-based
anywhere in the generated path. Use this when you want individual fields
addressable by plain Redis commands (`HGET`, `HINCRBY`, ...), not just an
opaque blob.

Because the mapping is generated for *your own* `[RedisHashModel]` types,
your project needs a direct analyzer reference to the generator itself, in
addition to referencing `ReadUs.Extensions.Hashes` for the attributes and
the `HSET`/`HGETALL` helper methods — referencing `ReadUs.Extensions.Hashes`
alone is not enough, since that only runs the generator against *its own*
types, not yours:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/ReadUs.Extensions.Hashes.csproj" />
  <ProjectReference Include="path/to/ReadUs.SourceGenerators.csproj"
                     OutputItemType="Analyzer"
                     ReferenceOutputAssembly="false" />
</ItemGroup>
```

```csharp
using ReadUs.Extensions.Hashes;

[RedisHashModel]
public partial class Account
{
    public string Name { get; set; } = "";
    public int Balance { get; set; }
    [RedisHashIgnore]
    public string? Notes { get; set; } // excluded from the mapping
}

await client.SetHashAsync("account:1"u8.ToArray(), new Account { Name = "Alice", Balance = 100 });
var account = await client.GetHashAsync<Account>("account:1"u8.ToArray()); // null if the key doesn't exist
```

The type must be `partial` and have either a public parameterless
constructor (a plain mutable POCO, as above) or a public constructor whose
parameters exactly match every mapped property by name (a positional
`record`) — anything else is a compile-time diagnostic, not a runtime
surprise. Supported property types: `string`, `byte[]`, `int`, `long`,
`double`, `bool`, `Guid`, `DateTime`, any `enum`, and a nullable of any of
those. A missing field throws on read unless the property is nullable
(then it comes back `null`); a null-valued nullable property is simply
skipped on write rather than writing an empty-string sentinel — Redis
hashes are naturally sparse, so "field absent" is already the correct
representation of "no value."

## `ReadUs.Extensions.Json` — whole-value JSON

Serializes an entire value as one JSON blob through ordinary `SET`/`GET`,
using `System.Text.Json`'s own source-generated `JsonTypeInfo<T>` — also
zero reflection, via a `JsonSerializerContext` you define yourself:

```csharp
using System.Text.Json.Serialization;
using ReadUs.Extensions.Json;

[JsonSerializable(typeof(Order))]
internal partial class AppJsonContext : JsonSerializerContext;

public record Order(string Id, decimal Total);

await client.SetJsonAsync("order:1"u8.ToArray(), new Order("1", 42.50m), AppJsonContext.Default.Order);
var order = await client.GetJsonAsync("order:1"u8.ToArray(), AppJsonContext.Default.Order);
```

An earlier version of this package also offered a `JsonSerializerOptions`
overload (ordinary runtime reflection) as a lower-friction fallback for
callers without a `JsonSerializerContext` set up. It was removed once
"zero reflection everywhere in this client's surface, including the
optional convenience packages" became the actual bar — `JsonTypeInfo<T>`
is the only path now.

Only whole-value `GET`/`SET` is covered here — for individual fields, use
`ReadUs.Extensions.Hashes` instead.

## See also

- [Getting started](getting-started.md) — the underlying `RedisResult`
  both of these build on.
