# ReadUs.Console — Chaos Runner for Redis client

This console application includes a Chaos Runner that helps test the resilience of the `ReadUs` Redis client against a local Redis cluster running in Docker.

The runner can:
- Bring up a local Redis cluster using the included `extras/redis-cluster/docker-compose.yml`.
- Continuously exercise your client by performing Set/Get operations via `RedisConnectionPool`.
- Randomly perform chaos actions against cluster nodes such as stop, start, restart, and `CLUSTER FAILOVER` (via `docker exec`).
- Print diagnostic information (cluster nodes) and log operations that fail.
- Tear down the cluster when finished or on Ctrl+C.

Quick start

1. Ensure Docker is installed and accessible to your user.
2. From the repository root run the console app:

```bash
dotnet run --project ReadUs.Console/ReadUs.Console.csproj
```

This will:
- Start the compose stack defined at `extras/redis-cluster/docker-compose.yml`.
- Run the client loop and random chaos actions (default ~60 iterations, 5s apart).
- Tear down the cluster when finished or on Ctrl+C.

Configuration

The runner supports a few environment variables to make experimentation easier:

- `READUS_COMPOSE_PATH` — Path to docker-compose.yml to use (defaults to `extras/redis-cluster/docker-compose.yml` resolved from project layout).
- `READUS_CHAOS_ITERATIONS` — Number of chaos actions to perform before stopping the run (default: 60).
- `READUS_CHAOS_DELAY_MS` — Milliseconds to wait between chaos actions (default: 5000).
- `READUS_NODES` — Comma-separated list of Docker container names to target (default: `redis-node-1,redis-node-2,...,redis-node-6`).

Example (faster, 10 iterations):

```bash
READUS_CHAOS_ITERATIONS=10 READUS_CHAOS_DELAY_MS=1000 dotnet run --project ReadUs.Console/ReadUs.Console.csproj
```

Safety and permissions

- The runner calls the `docker` CLI, so the current user must be able to run docker commands (or run the console app with appropriate privileges).
- The runner will attempt to bring the compose stack down on exit — if the process is killed forcefully the compose stack may remain running and must be cleaned manually.

Extending the chaos actions

The code includes a `RunClusterFailover` action that runs `redis-cli` inside a container. You can add additional actions such as:
- Resharding operations (using `redis-cli --cluster reshard` with slot ranges) — note these can be disruptive and slow.
- Simulating network partitions using `tc` inside containers or modifying Docker network rules.
- Forcing role changes or promoting replicas explicitly.
