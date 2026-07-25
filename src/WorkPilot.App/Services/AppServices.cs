using Microsoft.Data.Sqlite;
using WorkPilot.App.Core.Security;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Application.Security;
using WorkPilot.Application.Security.Governance;
using WorkPilot.Application.Diagnostics;
using WorkPilot.Application.Security.Retention;
using WorkPilot.Domain.Automation.Run.Redaction;
using WorkPilot.Domain.Security.Detectors;
using WorkPilot.Infrastructure.Automation;
using WorkPilot.Infrastructure.Clock;
using WorkPilot.Infrastructure.Ids;
using WorkPilot.Infrastructure.Permission.Policy;
using WorkPilot.Infrastructure.Random;
using WorkPilot.Infrastructure.Security;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AppServices : IAsyncDisposable
{
    public DatabaseService Database { get; }
    /// <summary>Permission page view-model (PER-003/004/008–010, T18). Wires the SQLite policy/grant
    /// store and Application policy services into the BCL <c>PolicyPermissionsViewModel</c>.</summary>
    public WorkPilot.App.Core.Permissions.PolicyPermissionsViewModel Permissions { get; private set; } = null!;
    /// <summary>Security Center view-model (SEC-101–107 / PER-008, T20). Composes the Application
    /// governance services and Infrastructure security stores behind the BCL facade
    /// <see cref="SecurityCenterDataProvider"/>. The WinUI <c>SecurityCenterPage</c> binds only to this.</summary>
    public SecurityCenterViewModel SecurityCenter { get; private set; } = null!;
    /// <summary>Detector engine wired for the host (records remediation actions; no auto side effects).</summary>
    public DetectorEngine Detector { get; private set; } = null!;
    public ProjectRepository Projects { get; }
    public SpaceService Spaces { get; }
    public TaskService Tasks { get; }
    public AutomationRepository Automations { get; }
    public SecretService Secrets { get; }
    public INativeWorkspaceFactory Native { get; }
    public AssetRepository Assets { get; }
    public AssetSearchService AssetSearch { get; }
    public AssetIndexCoordinator AssetIndex { get; }
    public ExpertService Experts { get; }
    public SkillService Skills { get; }
    public ConnectorService Connectors { get; }
    public McpService Mcp { get; }
    public CapabilityRuntimeService Capabilities { get; }
    public AgentService Agent { get; }
    public AutomationScheduler Scheduler { get; }
    public AppSettings Settings { get; private set; }
    public Space ActiveSpace { get; private set; }
    public event EventHandler<Space>? ActiveSpaceChanged;
    public string? PendingConversationId { get; private set; }
    public string? PendingPrompt { get; private set; }
    /// <summary>Activated structured diagnostic logger (T14 closure). Disposed on shutdown.</summary>
    private IDiagnosticLogger? _diagnosticLogger;

    private AppServices(DatabaseService database, ProjectRepository projects, SpaceService spaces,
        TaskService tasks, AutomationRepository automations, SecretService secrets,
        INativeWorkspaceFactory native, AssetRepository assets, AssetSearchService assetSearch,
        AssetIndexCoordinator assetIndex, ExpertService experts, SkillService skills,
        ConnectorService connectors, McpService mcp, CapabilityRuntimeService capabilities,
        AgentService agent, AutomationScheduler scheduler,
        AppSettings settings, Space activeSpace)
    {
        Database = database; Projects = projects; Spaces = spaces; Tasks = tasks; Automations = automations;
        Secrets = secrets; Native = native; Assets = assets; AssetSearch = assetSearch; AssetIndex = assetIndex;
        Experts = experts; Skills = skills; Connectors = connectors; Mcp = mcp; Capabilities = capabilities;
        Agent = agent; Scheduler = scheduler; Settings = settings; ActiveSpace = activeSpace;
    }

    public static async Task<AppServices> CreateAsync()
    {
        var database = new DatabaseService(); await database.InitializeAsync();
        var settings = await database.LoadSettingsAsync(); var spaces = new SpaceService(database);
        var activeSpace = await spaces.EnsureActiveAsync(settings.ActiveSpaceId);
        settings = await database.LoadSettingsAsync();
        var projects = new ProjectRepository(database); var tasks = new TaskService(database);
        var automations = new AutomationRepository(); var secrets = new SecretService();
        INativeWorkspaceFactory native = new NativeWorkspaceFactory(); var assets = new AssetRepository(database);
        var search = new AssetSearchService(new AssetSearchRepository(database), projects, assets, native);
        var index = new AssetIndexCoordinator(database, assets, native);
        var experts = new ExpertService(database); var skills = new SkillService(database);
        var connectors = new ConnectorService(database, secrets); var mcp = new McpService(database, secrets);
        var capabilities = new CapabilityRuntimeService(database, connectors, mcp);
        var contexts = new AgentContextService(database, experts, skills);
        var agent = new AgentService(database, secrets, new OpenAiClient(), native, search,
            experts, contexts, capabilities);
        var scheduler = new AutomationScheduler(automations, database, projects, agent);
        var services = new AppServices(database, projects, spaces, tasks, automations, secrets, native,
            assets, search, index, experts, skills, connectors, mcp, capabilities,
            agent, scheduler, settings, activeSpace);

        // Secret-scanning profile (stable DPAPI key + canaries + known-secret matcher), shared by the
        // support-bundle scanner and the T14 diagnostic logger so both redact with the same key.
        var scanningProvider = new SecretScanningKeyProvider();
        var scanningKey = scanningProvider.LoadOrCreateKey();
        var scanProfile = scanningProvider.BuildProfile(scanningKey);
        var knownSecrets = new List<string>();
        var apiKey = secrets.LoadApiKey();
        if (!string.IsNullOrEmpty(apiKey)) knownSecrets.Add(apiKey);
        var secretMatcher = scanProfile.BuildMatcher(knownSecrets);

        services.Permissions = BuildPermissionsViewModel(database);
        var diagnosticDir = new DiagnosticLogDirectory();
        var (securityCenter, detector) = BuildSecurityCenter(
            database, connectors, mcp, automations, secrets, scanProfile, secretMatcher, diagnosticDir);
        services.SecurityCenter = securityCenter;
        services.Detector = detector;

        // T14 closure: activate the structured JSONL diagnostic logger with the same canary set (Stage-7,
        // releaseMode strict) and known-secret matcher (Stage-3). Previously a dead link because
        // AppDiagnostics.SetLogger was never called, so every diagnostic emit was a silent no-op.
        services._diagnosticLogger = new JsonlDiagnosticLogger(
            new FileLogSink(diagnosticDir.Directory, diagnosticDir.BaseName),
            secretMatcher, scanProfile.CanaryTokens, releaseMode: true);
        AppDiagnostics.SetLogger(services._diagnosticLogger);

        scheduler.Start(); return services;
    }

    /// <summary>Composes the permission page view-model from the SQLite store and Application services (T18).</summary>
    private static WorkPilot.App.Core.Permissions.PolicyPermissionsViewModel BuildPermissionsViewModel(DatabaseService database)
    {
        var connection = database.OpenConnectionAsync().GetAwaiter().GetResult();
        var ids = new WorkPilot.Infrastructure.Ids.SortableIdGenerator(
            new WorkPilot.Infrastructure.Clock.SystemClock(),
            new WorkPilot.Infrastructure.Random.SystemRandomSource());
        var store = new WorkPilot.Infrastructure.Permission.Policy.SqlitePolicyStore(connection, ids);
        var simulator = new WorkPilot.Application.Permission.Policy.PolicySimulatorService(store);
        var projection = new WorkPilot.Application.Permission.Policy.PolicyProjectionService(simulator);
        var impact = new WorkPilot.Application.Permission.Policy.PolicyImpactService(store);
        var permitCore = new WorkPilot.Application.Automation.Run.Permit.ManagedPermitCore(
            new WorkPilot.Infrastructure.Clock.SystemClock());
        var admin = new WorkPilot.Application.Permission.Policy.PolicyAdminService(store, impact, permitCore);
        var clock = new WorkPilot.Infrastructure.Clock.SystemClock();
        return new WorkPilot.App.Core.Permissions.PolicyPermissionsViewModel(store, projection, admin, store, clock);
    }

    /// <summary>
    /// Composes the Security Center view-model (T20c). One shared SQLite connection backs the security
    /// stores (incident/event/audit, security_state, revocation_epoch, grants) so they observe a
    /// consistent snapshot. Host-provided ports (<see cref="SecuritySourceGovernanceBackend"/> for
    /// source toggling/health, <see cref="RecordingDetectorActionExecutor"/> for detector remediation)
    /// are wired here; the detector engine is exposed on <see cref="Detector"/> for the host to run.
    /// WinUI compilation is gated to a real Windows build (doc 10 §16).
    /// </summary>
    private static (SecurityCenterViewModel, DetectorEngine) BuildSecurityCenter(
        DatabaseService database, ConnectorService connectors, McpService mcp, IAutomationRepository automations,
        SecretService secretService, SecretScanningProfile scanProfile, ISecretMatcher secretMatcher,
        IDiagnosticLogDirectory diagnosticDir)
    {
        var connection = database.OpenConnectionAsync().GetAwaiter().GetResult();
        var clock = new SystemClock();
        var ids = new SortableIdGenerator(clock, new SystemRandomSource());

        var securityStore = new SecuritySqliteStore(connection);   // IIncidentStore + ISecurityEventStore + IAuditLogStore + IDetectorActionStore
        var stateStore = new SecurityStateSqliteStore(connection);  // ISecurityStateStore
        var epoch = new SqliteRevocationEpoch(connection);          // IRevocationEpoch
        var policyStore = new SqlitePolicyStore(connection, ids);   // IGrantStore (+ IPolicyStore)

        var gate = new SlidingNotificationGate();
        var keyProvider = new StaticAuditKeyProvider();
        var audit = new AuditLogWriter(securityStore, keyProvider, clock);

        var aggregator = new IncidentAggregatorService(securityStore, securityStore, gate, null, clock, ids);
        var emitter = new SecurityEventSink(aggregator);
        var detectorExecutor = new RecordingDetectorActionExecutor();
        var detector = new DetectorEngine(DetectorRuleCatalog.All(ids), emitter, securityStore, detectorExecutor);

        var incidentGov = new IncidentGovernanceService(securityStore, aggregator, clock);
        var backend = new SecuritySourceGovernanceBackend(connectors, mcp);
        var sourceGov = new SourceGovernanceService(backend, epoch);
        var grantGov = new GrantGovernanceService(policyStore, epoch, clock, audit);
        var emergency = new EmergencyStopCoordinator(stateStore, epoch, automations, audit);

        // Retention / cleanup / export / support-package (doc 05 §9/§10.2, LOG-005/006, SEC-106/108).
        var retentionSettingsStore = new SqliteRetentionSettingsStore(connection, clock);
        var retentionStore = new SqliteRetentionStore(connection);
        var retentionSettings = new RetentionSettingsService(retentionSettingsStore);
        var cleaner = new DataRetentionCleaner(retentionSettingsStore, retentionStore, audit, clock);
        var runs = new RunRepository(connection);
        var runReportExporter = new RunReportExporter(runs, clock);
        var integrity = new AuditIntegrityService(securityStore, keyProvider, clock);
        var appInfo = new AppInfo();
        var optimize = new SqliteOptimizeDatabase(connection);

        // Support-bundle secret scan (LOG-A05 / SEC-A14): reuses the shared scan profile + matcher
        // built in CreateAsync so the Stage-3 known-secret matcher and Stage-7 canary set are
        // identical to the diagnostic logger's.
        var supportBuilder = new SupportBundleBuilder(
            securityStore, securityStore, backend, policyStore, runs, runReportExporter,
            integrity, diagnosticDir, appInfo, scanProfile.CanaryTokens, clock,
            retentionSettingsStore, secretMatcher);

        var provider = new SecurityCenterDataProvider(
            securityStore, incidentGov, backend, sourceGov, policyStore, grantGov, stateStore, emergency, securityStore,
            retentionSettings, cleaner, supportBuilder, runReportExporter);

        return (new SecurityCenterViewModel(provider), detector);
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await Database.SaveSettingsAsync(settings); Settings = settings;
    }

    public async Task SetActiveSpaceAsync(Space space)
    {
        await Spaces.SetActiveAsync(space); ActiveSpace = space;
        Settings = await Database.LoadSettingsAsync(); ActiveSpaceChanged?.Invoke(this, space);
    }

    public async Task<Project?> GetActiveProjectAsync() => Settings.ActiveProjectId is null ? null :
        await Projects.GetAsync(Settings.ActiveProjectId);

    public void OpenConversationDraft(string conversationId, string prompt)
    {
        PendingConversationId = conversationId; PendingPrompt = prompt;
    }

    public (string? ConversationId, string? Prompt) ConsumeConversationDraft()
    {
        var value = (PendingConversationId, PendingPrompt); PendingConversationId = null; PendingPrompt = null; return value;
    }

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync(); await Agent.DisposeAsync(); await Mcp.DisposeAsync();
        Connectors.Dispose(); await AssetIndex.DisposeAsync();
        // Flush + release the diagnostic sink (T14). JsonlDiagnosticLogger is IDisposable, not
        // IAsyncDisposable, so dispose synchronously on the async path.
        (_diagnosticLogger as IDisposable)?.Dispose();
        _diagnosticLogger = null;
    }
}
