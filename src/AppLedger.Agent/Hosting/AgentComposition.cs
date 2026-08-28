using AppLedger.Collector;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Core.Catalog;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Catalog;
using AppLedger.Infrastructure.Etw;
using AppLedger.Infrastructure.Gpu;
using AppLedger.Infrastructure.Network;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using AppLedger.Infrastructure.Process;
using AppLedger.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace AppLedger.Agent.Hosting;

/// <summary>Everything the Agent built, kept together so shutdown can take it all down in order.</summary>
/// <param name="Collector">The pipeline.</param>
/// <param name="Database">The connection factory, so the schema version can be reported.</param>
/// <param name="Repository">Where history goes.</param>
/// <param name="DataRoot">Where everything the Agent writes lives.</param>
/// <param name="Sensors">The sensors, in start order.</param>
/// <param name="CatalogVersion">The catalog that loaded, or null when none did.</param>
/// <param name="CatalogVerified">Whether its signature verified.</param>
/// <param name="SchemaVersion">The database schema version after migration.</param>
public sealed record AgentRuntime(
    CollectorHost Collector,
    SqliteConnectionFactory Database,
    IMetricsRepository Repository,
    DataRoot DataRoot,
    IReadOnlyList<ISensor> Sensors,
    string? CatalogVersion,
    bool CatalogVerified,
    int SchemaVersion) : IDisposable
{
    /// <summary>Disposes the sensors that hold unmanaged resources.</summary>
    public void Dispose()
    {
        foreach (var sensor in Sensors.OfType<IDisposable>())
        {
            sensor.Dispose();
        }
    }
}

/// <summary>
/// Builds the Agent's object graph by hand (docs/01_ARCHITECTURE.md §Collector pipeline).
/// </summary>
/// <remarks>
/// Written out rather than registered in a container, because the wiring <b>order</b> is part of the design
/// and a container hides it: the catalog has to load before <c>PolicyGuard</c> can classify anything, the
/// schema has to migrate before a repository opens a connection, and the ETW handlers have to be attached to
/// the accumulators before a single event arrives. Each of those is a silent wrong answer if it happens in
/// the wrong order, not an exception.
/// <para>
/// Two of them are shaped so they cannot be got wrong: <see cref="SensorJoin.Create"/> attaches the handlers
/// as part of constructing the accumulators, and <c>CollectorHost</c> owns the write ordering that keeps the
/// <c>metrics_1m</c> → <c>apps</c> foreign key satisfied.
/// </para>
/// </remarks>
public static class AgentComposition
{
    /// <summary>Builds the runtime.</summary>
    /// <param name="loggerFactory">For the adapters that log.</param>
    /// <param name="options">Collector tunables; defaults to the elevated profile.</param>
    /// <param name="dataRoot">Overridable so a test can point at a temp directory.</param>
    public static AgentRuntime Build(
        ILoggerFactory loggerFactory,
        CollectorOptions? options = null,
        DataRoot? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var folders = KnownFolders.Current;
        var root = dataRoot ?? DataRoot.Default;
        root.EnsureCreated();

        var (catalog, catalogVersion, catalogVerified) = LoadCatalog(root, loggerFactory);

        // PolicyGuard is built from the catalog, so it has to come after it. A guard built with a null
        // catalog is a working guard with fewer rules, not a broken one - which is what keeps a missing or
        // rejected catalog from taking the Agent down (docs/13_CATALOG_RULES.md).
        var policy = PolicyGuard.Create(catalog, root, folders);

        var database = new SqliteConnectionFactory(root, DatabaseOptions.Agent);
        var schemaVersion = new SchemaMigrator(database, root).Migrate();
        var repository = new MetricsRepository(database);

        var resolver = new FallbackIdentityResolver(
            policy,
            new InstallRootHeuristic(InstallRootBoundaries.For(folders)));
        var registry = new InstanceRegistry(policy, new ProcessEnricher(), resolver);

        var etw = EtwHub.CanCreateSessions ? new EtwHub(loggerFactory.CreateLogger<EtwHub>()) : null;
        var gpu = new GpuPoller();
        var connections = new ConnectionPoller();

        // Create attaches the ETW handlers to the accumulators. Building them separately and forgetting to
        // connect them produces no error at all - just an Agent that records zero bytes for everything.
        var join = SensorJoin.Create(etw, gpu);

        var collector = new CollectorHost(
            new NtProcessSource(),
            registry,
            SystemClock.Instance,
            options ?? new CollectorOptions(),
            repository,
            join);

        var sensors = new List<ISensor>();
        if (etw is not null)
        {
            sensors.Add(etw);
        }

        sensors.Add(gpu);
        sensors.Add(connections);

        foreach (var sensor in sensors)
        {
            collector.AddSensor(sensor);
        }

        return new AgentRuntime(
            collector,
            database,
            repository,
            root,
            sensors,
            catalogVersion,
            catalogVerified,
            schemaVersion);
    }

    private static (CatalogDocument? Catalog, string? Version, bool Verified) LoadCatalog(
        DataRoot root,
        ILoggerFactory loggerFactory)
    {
        var loader = CatalogLoader.TryCreateFromEmbeddedKey(
            new EnvExpander(KnownFolders.Current.CatalogVariables),
            loggerFactory.CreateLogger<CatalogLoader>());

        if (loader is null)
        {
            // No signing key was embedded in this build, so no catalog can be trusted. Rules are data and
            // the Agent runs without them rather than loading unsigned data (ADR-12).
            return (null, null, false);
        }

        var catalogPath = Path.Combine(root.CatalogDirectory, "appledger-catalog.json");
        var result = loader.Load(catalogPath, catalogPath + ".minisig");

        return (result.Document, result.Document?.Version, result.Succeeded);
    }
}
