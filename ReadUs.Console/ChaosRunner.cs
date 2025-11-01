// filepath: /home/tombatron/projects/ReadUs/ReadUs.Console/ChaosRunner.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Commands;
using Tombatron.Results;
using Cons = System.Console;

namespace ReadUs.Console;

public static class ChaosRunner
{
    private static readonly string[] DefaultNodes = new[] { "redis-node-1", "redis-node-2", "redis-node-3", "redis-node-4", "redis-node-5", "redis-node-6" };
    private static readonly Random Rnd = new();

    // Client health monitoring state
    private static long _lastSuccessTicks = DateTimeOffset.UtcNow.Ticks;
    private static int _consecutiveFailures = 0;

    public static async Task Run()
    {
        // Configuration via environment variables (can be extended to accept args later)
        var composeFile = Environment.GetEnvironmentVariable("READUS_COMPOSE_PATH")
                          ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "extras", "redis-cluster", "docker-compose.yml"));

        var iterationsEnv = Environment.GetEnvironmentVariable("READUS_CHAOS_ITERATIONS");
        var delayMsEnv = Environment.GetEnvironmentVariable("READUS_CHAOS_DELAY_MS");

        var iterations = 60;
        var delayMs = 5000;

        if (int.TryParse(iterationsEnv, out var i)) iterations = Math.Max(1, i);
        if (int.TryParse(delayMsEnv, out var d)) delayMs = Math.Max(250, d);

        // Health monitor configuration
        var failCountEnv = Environment.GetEnvironmentVariable("READUS_CLIENT_FAIL_COUNT");
        var failTimeoutEnv = Environment.GetEnvironmentVariable("READUS_CLIENT_FAIL_TIMEOUT_MS");
        var clientFailCount = 10;
        var clientFailTimeoutMs = 30000;
        if (int.TryParse(failCountEnv, out var fc)) clientFailCount = Math.Max(1, fc);
        if (int.TryParse(failTimeoutEnv, out var ft)) clientFailTimeoutMs = Math.Max(1000, ft);

        var nodesEnv = Environment.GetEnvironmentVariable("READUS_NODES");
        var nodes = string.IsNullOrWhiteSpace(nodesEnv) ? DefaultNodes : nodesEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Cons.WriteLine("Starting chaos runner...");
        Cons.WriteLine($"Using compose file: {composeFile}");
        Cons.WriteLine($"Iterations: {iterations}, DelayMs: {delayMs}, Nodes: {string.Join(',', nodes)}");
        Cons.WriteLine($"Client health: failCount={clientFailCount}, failTimeoutMs={clientFailTimeoutMs}");

        var cts = new CancellationTokenSource();

        // Ensure we tear down compose on Ctrl+C or process exit
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true; // allow graceful shutdown
            Cons.WriteLine("Cancellation requested - shutting down gracefully...");
            cts.Cancel();
        };

        Cons.CancelKeyPress += cancelHandler;

        try
        {
            var up = await RunDockerCompose($"-f \"{composeFile}\" up -d");
            Cons.WriteLine(up.output);
            if (up.exitCode != 0)
            {
                Cons.WriteLine($"docker compose up failed: {up.error}");
            }

            // start client loop
            var clientTask = Task.Run(() => ClientLoop(cts.Token));

            // start monitor task to watch client health
            var monitorTask = Task.Run(() => MonitorLoop(cts.Token, cts, clientFailCount, clientFailTimeoutMs));

            // Run chaos loop
            for (int iter = 0; iter < iterations && !cts.IsCancellationRequested; iter++)
            {
                await Task.Delay(delayMs, cts.Token).ContinueWith(_ => { });

                var node = nodes[Rnd.Next(nodes.Length)];
                var action = Rnd.Next(5); // more actions now

                try
                {
                    switch (action)
                    {
                        case 0:
                            await DockerCommand($"stop {node}");
                            break;
                        case 1:
                            await DockerCommand($"start {node}");
                            break;
                        case 2:
                            await DockerCommand($"restart {node}");
                            break;
                        case 3:
                            // Attempt a CLUSTER FAILOVER on the chosen node (may fail if node is master)
                            await RunClusterFailover(node);
                            break;
                        case 4:
                            // Diagnostic: print cluster nodes from the chosen node
                            await DockerCommand($"exec {node} redis-cli -c cluster nodes");
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // expected when cancelling
                }
                catch (Exception ex)
                {
                    Cons.WriteLine($"Chaos action failed: {ex}");
                }
            }

            // Request cancellation so client and monitor shut down
            cts.Cancel();

            // Wait for both client and monitor to complete (with a safety timeout)
            var wait = Task.WhenAll(clientTask, monitorTask);
            var finished = await Task.WhenAny(wait, Task.Delay(5000));
            if (finished != wait)
            {
                Cons.WriteLine("Warning: client/monitor did not finish in time after cancellation");
            }
        }
        finally
        {
            // Try to bring the compose stack down on exit
            try
            {
                var down = await RunDockerCompose($"-f \"{composeFile}\" down");
                Cons.WriteLine(down.output);
            }
            catch (Exception ex)
            {
                Cons.WriteLine($"Error during compose down: {ex}");
            }

            Cons.CancelKeyPress -= cancelHandler;
        }
    }

    static async Task ClientLoop(CancellationToken token)
    {
        var connectionString = new Uri("redis://localhost:6379");

        // create a pool that will be used to exercise the client while chaos is happening
        using var pool = RedisConnectionPool.Create(connectionString);

        while (!token.IsCancellationRequested)
        {
            var key = Guid.NewGuid().ToString("n");
            var value = Guid.NewGuid().ToString("n");

            try
            {
                var db = await pool.GetDatabase();

                var setRes = await db.Set(key, value);

                if (setRes is Error setErr)
                {
                    Cons.WriteLine($"Set error: {setErr.Message}");
                    System.Threading.Interlocked.Increment(ref _consecutiveFailures);
                }
                else if (setRes is Ok)
                {
                    var getRes = await db.Get(key);

                    if (getRes is Ok<string> ok)
                    {
                        Cons.WriteLine($"OK Get {key} -> {ok.Value}");
                        System.Threading.Interlocked.Exchange(ref _consecutiveFailures, 0);
                        System.Threading.Interlocked.Exchange(ref _lastSuccessTicks, DateTimeOffset.UtcNow.Ticks);
                    }
                    else if (getRes is Error<string> getErr)
                    {
                        Cons.WriteLine($"Get error: {getErr.Message}");
                        System.Threading.Interlocked.Increment(ref _consecutiveFailures);
                    }
                }
                else
                {
                    // Handle unexpected result shape defensively
                    Cons.WriteLine($"Set returned unexpected result type: {setRes?.GetType().FullName}");
                    System.Threading.Interlocked.Increment(ref _consecutiveFailures);
                }
            }
            catch (Exception ex)
            {
                Cons.WriteLine($"Client exception: {ex.Message}");
                System.Threading.Interlocked.Increment(ref _consecutiveFailures);
            }

            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                // cancelled - loop will exit
            }
        }
    }

    static async Task MonitorLoop(CancellationToken token, CancellationTokenSource cts, int failCountThreshold, int failTimeoutMs)
    {
        // Check periodically whether the client has been making progress
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var lastTicks = System.Threading.Interlocked.Read(ref _lastSuccessTicks);
            var lastSuccess = new DateTimeOffset(lastTicks, TimeSpan.Zero);
            var ageMs = (DateTimeOffset.UtcNow - lastSuccess).TotalMilliseconds;
            var failures = System.Threading.Interlocked.CompareExchange(ref _consecutiveFailures, 0, 0);

            if (failures >= failCountThreshold)
            {
                Cons.WriteLine($"Client unhealthy: {failures} consecutive failures >= threshold {failCountThreshold}. Cancelling run.");
                try { cts.Cancel(); } catch { }
                break;
            }

            if (ageMs >= failTimeoutMs)
            {
                Cons.WriteLine($"Client unhealthy: no successful operations for {ageMs}ms >= timeout {failTimeoutMs}ms. Cancelling run.");
                try { cts.Cancel(); } catch { }
                break;
            }
        }
    }

    static async Task RunClusterFailover(string node)
    {
        Cons.WriteLine($"Attempting CLUSTER FAILOVER on {node} (via docker exec)...");

        // Try a non-forced failover first, then a forced one if it failed
        var res = await RunProcessAsync("docker", $"exec {node} redis-cli -c cluster failover");
        Cons.WriteLine($"Failover attempt exit {res.exitCode}: {res.output}{res.error}");

        if (res.exitCode != 0)
        {
            Cons.WriteLine("Attempting forced failover...");
            res = await RunProcessAsync("docker", $"exec {node} redis-cli -c cluster failover force");
            Cons.WriteLine($"Forced failover exit {res.exitCode}: {res.output}{res.error}");
        }
    }

    static async Task DockerCommand(string args)
    {
        var cmd = $"docker {args}";
        Cons.WriteLine($"Running: {cmd}");

        var res = await RunProcessAsync("docker", args);

        Cons.WriteLine($"Exit {res.exitCode}: {res.output}{res.error}");
    }

    static async Task<(int exitCode, string output, string error)> RunDockerCompose(string args)
    {
        // Try 'docker compose' first, fall back to 'docker-compose' if necessary
        var res = await RunProcessAsync("docker", $"compose {args}");
        if (res.exitCode == 0)
            return res;

        return await RunProcessAsync("docker-compose", args);
    }

    static async Task<(int exitCode, string output, string error)> RunProcessAsync(string fileName, string args)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var p = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        p.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await p.WaitForExitAsync();

        return (p.ExitCode, sbOut.ToString(), sbErr.ToString());
    }
}
