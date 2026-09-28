# ReadUs.Extensions.Json

Whole-value JSON POCO helpers for [ReadUs](https://github.com/tombatron/ReadUs)
(`SetJsonAsync`/`GetJsonAsync` over `SET`/`GET`), using
`System.Text.Json`'s own source-generated `JsonTypeInfo<T>` only — zero
reflection.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Extensions.Json/ReadUs.Extensions.Json.csproj
```

## Example

```csharp
using System.Text.Json.Serialization;
using ReadUs.Extensions.Json;

[JsonSerializable(typeof(Order))]
internal partial class AppJsonContext : JsonSerializerContext;

public record Order(string Id, decimal Total);

await client.SetJsonAsync("order:1"u8.ToArray(), new Order("1", 42.50m), AppJsonContext.Default.Order);
var order = await client.GetJsonAsync("order:1"u8.ToArray(), AppJsonContext.Default.Order);
```

## Documentation

See the [Hash and JSON mapping guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/hash-and-json-mapping.md)
and the [root README](https://github.com/tombatron/ReadUs).
