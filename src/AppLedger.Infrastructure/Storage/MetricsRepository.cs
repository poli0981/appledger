using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using Dapper;

namespace AppLedger.Infrastructure.Storage;

/// <summary>
/// The SQLite implementation of <see cref="IMetricsRepository"/>.
/// </summary>
/// <remarks>
/// Column names and <see cref="MetricRow"/> property names are kept in step deliberately
/// (docs/06_DATA_MODEL.md), so Dapper's default mapping does the work and a rename on either side shows up
/// as a failing round-trip rather than as a silently null column.
/// </remarks>
public sealed class MetricsRepository : IMetricsRepository
{
    private const string Columns =
        "app_id, ts, runtime_s, procs, procs_max, cpu_pct, cpu_pct_max, cpu_user_ms, cpu_kernel_ms, "
        + "ws_private, ws_private_max, commit_bytes, ws, gpu_pct, vram_ded, vram_ded_max, vram_shared, "
        + "io_read, io_write, disk_read, disk_write, disk_ops, net_in, net_out, net_in_loopback, "
        + "net_out_loopback, threads, handles, hard_faults, degraded";

    private const string Parameters =
        "$app_id, $ts, $runtime_s, $procs, $procs_max, $cpu_pct, $cpu_pct_max, $cpu_user_ms, $cpu_kernel_ms, "
        + "$ws_private, $ws_private_max, $commit_bytes, $ws, $gpu_pct, $vram_ded, $vram_ded_max, $vram_shared, "
        + "$io_read, $io_write, $disk_read, $disk_write, $disk_ops, $net_in, $net_out, $net_in_loopback, "
        + "$net_out_loopback, $threads, $handles, $hard_faults, $degraded";

    private readonly SqliteConnectionFactory _factory;

    /// <summary>Creates a repository over one database.</summary>
    public MetricsRepository(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task UpsertAppAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();

        // first_seen_utc is never overwritten: an app is first seen once, and letting an update move it
        // would quietly rewrite the beginning of its history.
        const string Sql = """
            INSERT INTO apps (
                app_id, display_name, publisher, category, category_source, source, confidence,
                install_root, current_version, signer, sig_status, first_seen_utc, last_seen_utc, tier)
            VALUES (
                $app_id, $display_name, $publisher, $category, $category_source, $source, $confidence,
                $install_root, $current_version, $signer, $sig_status, $first_seen_utc, $last_seen_utc, $tier)
            ON CONFLICT(app_id) DO UPDATE SET
                display_name    = excluded.display_name,
                publisher       = excluded.publisher,
                category        = excluded.category,
                category_source = excluded.category_source,
                source          = excluded.source,
                confidence      = excluded.confidence,
                install_root    = excluded.install_root,
                current_version = excluded.current_version,
                signer          = excluded.signer,
                sig_status      = excluded.sig_status,
                last_seen_utc   = excluded.last_seen_utc,
                tier            = excluded.tier;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            Sql,
            new
            {
                app_id = app.AppId.Value,
                display_name = app.DisplayName,
                publisher = app.Publisher,
                category = app.Category,
                category_source = app.CategorySource,
                source = app.Source.ToPrefix(),
                confidence = app.Confidence,
                install_root = app.InstallRoot,
                current_version = app.CurrentVersion,
                signer = app.Signer,
                sig_status = app.SignatureStatus.ToString(),
                first_seen_utc = app.FirstSeenUtc,
                last_seen_utc = app.LastSeenUtc,
                tier = (int)app.Tier,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteRowsAsync(
        MetricTier tier,
        IReadOnlyList<MetricRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        // INSERT OR REPLACE rather than INSERT: a rollup that runs twice for the same bucket must produce
        // the same table, not a duplicate-key error and not two rows (docs/06_DATA_MODEL.md §Rollup jobs).
        var sql = $"INSERT OR REPLACE INTO {TableOf(tier)} ({Columns}) VALUES ({Parameters});";

        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                ToParameters(row),
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteHealthAsync(HealthMinute minute, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();

        // Replace rather than insert: a restart inside the same minute must leave one row for it, not two.
        // ts is the primary key, so the conflict target is implicit.
        const string Sql = """
            INSERT INTO health_minutes (ts, agent_cpu_pct, agent_ws, events_lost, sensors_json)
            VALUES ($ts, $cpu, $ws, $lost, $sensors)
            ON CONFLICT(ts) DO UPDATE SET
                agent_cpu_pct = excluded.agent_cpu_pct,
                agent_ws      = excluded.agent_ws,
                events_lost   = excluded.events_lost,
                sensors_json  = excluded.sensors_json;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            Sql,
            new
            {
                ts = minute.TsUtc,
                cpu = minute.AgentCpuPct,
                ws = minute.AgentWs,
                lost = minute.EventsLost,
                sensors = minute.SensorsJson,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricRow>> ReadRangeAsync(
        AppId appId,
        MetricTier tier,
        long fromTsUtc,
        long toTsUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();

        // Half-open so consecutive ranges neither overlap nor leave a gap.
        var sql = $"SELECT {Columns} FROM {TableOf(tier)} "
            + "WHERE app_id = $app_id AND ts >= $from AND ts < $to ORDER BY ts;";

        var rows = await connection.QueryAsync<MetricRowDto>(new CommandDefinition(
            sql,
            new { app_id = appId.Value, from = fromTsUtc, to = toTsUtc },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(dto => dto.ToRow(appId))];
    }

    private static string TableOf(MetricTier tier) => tier switch
    {
        MetricTier.Minute => "metrics_1m",
        MetricTier.Hour => "metrics_1h",
        MetricTier.Day => "metrics_1d",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown metric tier."),
    };

    private static object ToParameters(MetricRow row) => new
    {
        app_id = row.AppId.Value,
        ts = row.Ts,
        runtime_s = row.RuntimeSeconds,
        procs = row.Procs,
        procs_max = row.ProcsMax,
        cpu_pct = row.CpuPct,
        cpu_pct_max = row.CpuPctMax,
        cpu_user_ms = row.CpuUserMs,
        cpu_kernel_ms = row.CpuKernelMs,
        ws_private = row.WsPrivate,
        ws_private_max = row.WsPrivateMax,
        commit_bytes = row.CommitBytes,
        ws = row.Ws,
        gpu_pct = row.GpuPct,
        vram_ded = row.VramDedicated,
        vram_ded_max = row.VramDedicatedMax,
        vram_shared = row.VramShared,
        io_read = row.IoRead,
        io_write = row.IoWrite,
        disk_read = row.DiskRead,
        disk_write = row.DiskWrite,
        disk_ops = row.DiskOps,
        net_in = row.NetIn,
        net_out = row.NetOut,
        net_in_loopback = row.NetInLoopback,
        net_out_loopback = row.NetOutLoopback,
        threads = row.Threads,
        handles = row.Handles,
        hard_faults = row.HardFaults,
        degraded = row.Degraded ? 1 : 0,
    };

    /// <summary>
    /// The row shape Dapper materializes. <see cref="MetricRow"/> itself has an <c>AppId</c> that is a
    /// value type over a string and <c>Degraded</c> as a bool, neither of which SQLite hands back
    /// directly, so the translation lives in one place instead of in a custom type handler.
    /// </summary>
    private sealed class MetricRowDto
    {
#pragma warning disable CA1707 // Column names, not identifiers: they mirror docs/06_DATA_MODEL.md.
#pragma warning disable IDE1006
        public long ts { get; init; }

        public long runtime_s { get; init; }

        public double procs { get; init; }

        public long procs_max { get; init; }

        public double cpu_pct { get; init; }

        public double cpu_pct_max { get; init; }

        public long cpu_user_ms { get; init; }

        public long cpu_kernel_ms { get; init; }

        public long ws_private { get; init; }

        public long ws_private_max { get; init; }

        public long commit_bytes { get; init; }

        public long ws { get; init; }

        public double gpu_pct { get; init; }

        public long vram_ded { get; init; }

        public long vram_ded_max { get; init; }

        public long vram_shared { get; init; }

        public long io_read { get; init; }

        public long io_write { get; init; }

        public long disk_read { get; init; }

        public long disk_write { get; init; }

        public long disk_ops { get; init; }

        public long net_in { get; init; }

        public long net_out { get; init; }

        public long net_in_loopback { get; init; }

        public long net_out_loopback { get; init; }

        public double threads { get; init; }

        public double handles { get; init; }

        public long hard_faults { get; init; }

        public long degraded { get; init; }
#pragma warning restore IDE1006
#pragma warning restore CA1707

        internal MetricRow ToRow(AppId appId) => new()
        {
            AppId = appId,
            Ts = ts,
            RuntimeSeconds = (int)runtime_s,
            Procs = procs,
            ProcsMax = (int)procs_max,
            CpuPct = cpu_pct,
            CpuPctMax = cpu_pct_max,
            CpuUserMs = cpu_user_ms,
            CpuKernelMs = cpu_kernel_ms,
            WsPrivate = ws_private,
            WsPrivateMax = ws_private_max,
            CommitBytes = commit_bytes,
            Ws = ws,
            GpuPct = gpu_pct,
            VramDedicated = vram_ded,
            VramDedicatedMax = vram_ded_max,
            VramShared = vram_shared,
            IoRead = io_read,
            IoWrite = io_write,
            DiskRead = disk_read,
            DiskWrite = disk_write,
            DiskOps = disk_ops,
            NetIn = net_in,
            NetOut = net_out,
            NetInLoopback = net_in_loopback,
            NetOutLoopback = net_out_loopback,
            Threads = threads,
            Handles = handles,
            HardFaults = hard_faults,
            Degraded = degraded != 0,
        };
    }
}
