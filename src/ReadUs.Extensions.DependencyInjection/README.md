# ReadUs.Extensions.DependencyInjection

`Microsoft.Extensions.DependencyInjection` registration helpers for
[ReadUs](https://github.com/tombatron/ReadUs)'s `RedisClient`,
`ClusterClient`, and `SentinelClient`.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Extensions.DependencyInjection/ReadUs.Extensions.DependencyInjection.csproj
```

## Example

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReadUs.Connections;
using ReadUs.Extensions.DependencyInjection;

services.AddReadUsClient(new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });
```

## Documentation

See the [Dependency injection and observability guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/dependency-injection-and-observability.md)
and the [root README](https://github.com/tombatron/ReadUs).
