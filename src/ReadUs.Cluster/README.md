# ReadUs.Cluster

Redis Cluster support for [ReadUs](https://github.com/tombatron/ReadUs):
hash-slot routing, transparent `MOVED`/`ASK`/`TRYAGAIN`/`CLUSTERDOWN`
handling, per-node health tracking with quarantine and automatic
reintegration, replica read routing, cluster-routed transactions and
blocking commands, and sharded Pub/Sub.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Cluster/ReadUs.Cluster.csproj
```

## Example

```csharp
using System.Net;
using ReadUs.Cluster;

await using var cluster = await ClusterClient.ConnectAsync(
    seedEndpoints: [new DnsEndPoint("localhost", 7001), new DnsEndPoint("localhost", 7002)]);

await cluster.ExecuteAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
```

## Documentation

See the [Cluster guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/cluster.md)
and the [root README](https://github.com/tombatron/ReadUs).
