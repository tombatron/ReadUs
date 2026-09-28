# ReadUs.Sentinel

Redis Sentinel support for [ReadUs](https://github.com/tombatron/ReadUs):
quorum-of-responders master discovery, dual pub/sub + polling failover
detection, and automatic reconnect to the current master.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Sentinel/ReadUs.Sentinel.csproj
```

## Example

```csharp
using System.Net;
using ReadUs.Sentinel;

await using var sentinel = await SentinelClient.ConnectAsync(
    sentinelEndpoints: [new DnsEndPoint("localhost", 26379)],
    serviceName: "mymaster");

await sentinel.ExecuteAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
```

## Documentation

See the [Sentinel guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/sentinel.md)
and the [root README](https://github.com/tombatron/ReadUs).
