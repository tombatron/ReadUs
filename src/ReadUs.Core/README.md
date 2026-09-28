# ReadUs.Core

The standalone Redis client: RESP3 protocol handling, Tier 1 (multiplexed)
and Tier 2 (leased) connection pooling, transactions, blocking commands,
client-side caching, Pub/Sub, scripting, and the source-generated typed
command surface. Everything else in ReadUs (`ReadUs.Cluster`,
`ReadUs.Sentinel`, and the optional extension packages) builds on this.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Core/ReadUs.Core.csproj
```

## Example

```csharp
using System.Net;
using ReadUs;
using ReadUs.Connections;
using ReadUs.Generated;

await using var client = await RedisClient.ConnectAsync(
    new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });

await client.SetAsync("key"u8.ToArray(), "value"u8.ToArray());
var result = await client.GetAsync("key"u8.ToArray());
```

## Documentation

See the [full guide set](https://github.com/tombatron/ReadUs/tree/main/docs/guides)
and the [root README](https://github.com/tombatron/ReadUs) — start with
[Getting started](https://github.com/tombatron/ReadUs/blob/main/docs/guides/getting-started.md).
