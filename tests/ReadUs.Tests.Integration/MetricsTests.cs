using System.Diagnostics.Metrics;
using ReadUs.Connections;
using ReadUs.Diagnostics;
using ReadUs.Pooling;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Verifies ReadUs's metrics (project spec §10) actually emit correctly, using the
/// BCL's <see cref="MeterListener"/> directly rather than pulling the OpenTelemetry SDK
/// into this test project — proving the "zero dependency in ReadUs.Core" design works
/// on its own terms, the same way an application's own OpenTelemetry pipeline would
/// observe these instruments once <c>ReadUs.Extensions.OpenTelemetry</c> points a real
/// <c>MeterProvider</c> at <see cref="ReadUsDiagnostics.MeterName"/>. Runs against a
/// disposable Testcontainers-managed server (project spec §9.2).
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class MetricsTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task ConnectionLifecycleAndCommandMetricsAreRecorded()
    {
        var measurements = new List<(string Instrument, object Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReadUsDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => measurements.Add((instrument.Name, value)));
        listener.Start();

        await using (var connection = await RedisConnection.ConnectAsync(Options))
        {
            await connection.SendAsync("PING"u8.ToArray(), []);
        }

        listener.Dispose();

        Assert.Contains(measurements, m => m.Instrument == "readus.connections.opened" && Equals(m.Value, 1L));
        Assert.Contains(measurements, m => m.Instrument == "readus.connections.active" && Equals(m.Value, 1));
        Assert.Contains(measurements, m => m.Instrument == "readus.command.duration");
        Assert.Contains(measurements, m => m.Instrument == "readus.commands.executed" && Equals(m.Value, 1L));

        // DisposeAsync faults the connection (project spec's own documented shutdown
        // path) — that must show up too, including the active-count decrement.
        Assert.Contains(measurements, m => m.Instrument == "readus.connections.faulted" && Equals(m.Value, 1L));
        Assert.Contains(measurements, m => m.Instrument == "readus.connections.active" && Equals(m.Value, -1));
    }

    [Fact]
    public async Task PoolReconnectIsRecordedWhenAFaultedSlotIsHealed()
    {
        var reconnectCount = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReadUsDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "readus.pool.reconnects")
            {
                Interlocked.Add(ref reconnectCount, (int)value);
            }
        });
        listener.Start();

        await using (var pool = await MultiplexedConnectionPool.CreateAsync(Options, size: 1))
        {
            await pool.Rent().DisposeAsync();

            for (var i = 0; i < 50 && Volatile.Read(ref reconnectCount) == 0; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        listener.Dispose();

        // >= 1, not == 1: the underlying meter is a single process-wide static (a
        // deliberate scoping choice, see docs/design/state-machines.md §5), so a
        // concurrently-running test's own pool healing can add to this count too.
        Assert.True(reconnectCount >= 1, $"Expected at least one reconnect to be recorded; saw {reconnectCount}.");
    }
}
