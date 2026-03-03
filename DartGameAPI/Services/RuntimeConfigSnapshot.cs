using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DartGameAPI.Services;

/// <summary>
/// Phase 43Y: Runtime Integrity Lockdown - captures all flag states at a point in time.
/// </summary>
public class RuntimeConfigSnapshot
{
    [JsonPropertyName("build_id")] public string BuildId { get; set; } = "";
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
    [JsonPropertyName("use_hhs")] public bool UseHHS { get; set; }
    [JsonPropertyName("use_whrs")] public bool UseWHRS { get; set; }
    [JsonPropertyName("use_bcwt")] public bool UseBCWT { get; set; }
    [JsonPropertyName("use_radial_clamp")] public bool UseRadialClamp { get; set; }
    [JsonPropertyName("use_iqdl")] public bool UseIQDL { get; set; }
    [JsonPropertyName("use_dcwo")] public bool UseDCWO { get; set; }
    [JsonPropertyName("config_hash")] public string ConfigHash { get; set; } = "";
    [JsonPropertyName("enabled_stack")] public List<string> EnabledStack { get; set; } = new();

    /// <summary>
    /// Capture current flag states from the DLL by probing set-flag with current values.
    /// Since we can't read flags directly, we rely on defaults being ON (Phase 43Y).
    /// We track what's enabled based on our knowledge of defaults + any overrides.
    /// </summary>
    public static RuntimeConfigSnapshot Capture(
        bool useHHS = true, bool useWHRS = true, bool useBCWT = true,
        bool useRadialClamp = true, bool useIQDL = true, bool useDCWO = true)
    {
        var snap = new RuntimeConfigSnapshot
        {
            BuildId = $"phase43y-ril-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Timestamp = DateTime.UtcNow,
            UseHHS = useHHS,
            UseWHRS = useWHRS,
            UseBCWT = useBCWT,
            UseRadialClamp = useRadialClamp,
            UseIQDL = useIQDL,
            UseDCWO = useDCWO
        };

        snap.EnabledStack = snap.GetEnabledStack();
        snap.ConfigHash = snap.ComputeHash();
        return snap;
    }

    public List<string> GetEnabledStack()
    {
        var stack = new List<string>();
        if (UseHHS) stack.Add("HHS");
        if (UseWHRS) stack.Add("WHRS");
        if (UseBCWT) stack.Add("BCWT");
        if (UseRadialClamp) stack.Add("RadialClamp");
        if (UseIQDL) stack.Add("IQDL");
        if (UseDCWO) stack.Add("DCWO");
        return stack;
    }

    public string ComputeHash()
    {
        var concat = $"{UseHHS}|{UseWHRS}|{UseBCWT}|{UseRadialClamp}|{UseIQDL}|{UseDCWO}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(concat));
        return Convert.ToHexString(bytes)[..16].ToLower();
    }

    /// <summary>
    /// Validate flag combinations. Returns list of issues.
    /// </summary>
    public (bool passed, List<string> warnings, List<string> errors) ValidateAssertions()
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        if (!UseBCWT)
            warnings.Add("BCWT is OFF - scoring running without barrel-confidence weighted triangulation");

        if (UseWHRS && !UseHHS)
            errors.Add("WHRS is ON but HHS is OFF - invalid combination (WHRS depends on HHS)");

        if (UseDCWO && !UseWHRS)
            errors.Add("DCWO is ON but WHRS is OFF - invalid combination (DCWO depends on WHRS)");

        bool passed = errors.Count == 0 && warnings.Count == 0;
        return (passed, warnings, errors);
    }
}

/// <summary>
/// Singleton service that holds the current runtime config and tracks flag changes.
/// </summary>
public class RuntimeIntegrityService
{
    private readonly ILogger<RuntimeIntegrityService> _logger;
    private RuntimeConfigSnapshot _currentSnapshot;
    private readonly object _lock = new();

    // Track actual flag states (mirrors DLL defaults, updated on set-flag calls)
    private bool _useHHS = true;
    private bool _useWHRS = true;
    private bool _useBCWT = true;
    private bool _useRadialClamp = true;
    private bool _useIQDL = true;
    private bool _useDCWO = true;

    public RuntimeIntegrityService(ILogger<RuntimeIntegrityService> logger)
    {
        _logger = logger;
        _currentSnapshot = RefreshSnapshot();
    }

    public RuntimeConfigSnapshot CurrentSnapshot
    {
        get { lock (_lock) return _currentSnapshot; }
    }

    /// <summary>
    /// Called when a flag is changed via the API to keep our tracking in sync.
    /// </summary>
    public void OnFlagChanged(string flagName, int value)
    {
        lock (_lock)
        {
            bool bval = value != 0;
            switch (flagName)
            {
                case "UseHHS": _useHHS = bval; break;
                case "UseWHRS": _useWHRS = bval; break;
                case "UseBarrelConfidenceWeightedTriangulation": _useBCWT = bval; break;
                case "UseBCWTRadialStabilityClamp": _useRadialClamp = bval; break;
                case "UseIQDL": _useIQDL = bval; break;
                case "UseDCWO": _useDCWO = bval; break;
            }
            _currentSnapshot = RefreshSnapshot();
            _logger.LogInformation("[RIL] Flag {Flag}={Value}, new config_hash={Hash}, stack=[{Stack}]",
                flagName, value, _currentSnapshot.ConfigHash, string.Join(",", _currentSnapshot.EnabledStack));
        }
    }

    private RuntimeConfigSnapshot RefreshSnapshot()
    {
        return RuntimeConfigSnapshot.Capture(_useHHS, _useWHRS, _useBCWT, _useRadialClamp, _useIQDL, _useDCWO);
    }

    /// <summary>
    /// Run pre-scoring assertions. Returns (passed, softFailOk).
    /// </summary>
    public (bool assertionsPassed, List<string> issues) RunPreScoringAssertions()
    {
        var snap = CurrentSnapshot;
        var (passed, warnings, errors) = snap.ValidateAssertions();
        var issues = new List<string>();

        foreach (var w in warnings)
        {
            _logger.LogWarning("[RIL-ASSERT] {Warning}", w);
            issues.Add($"WARN: {w}");
        }
        foreach (var e in errors)
        {
            _logger.LogError("[RIL-ASSERT] {Error}", e);
            issues.Add($"ERROR: {e}");
        }

        return (passed, issues);
    }

    /// <summary>
    /// Write startup snapshot to disk.
    /// </summary>
    public void WriteStartupSnapshot()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DartDetector");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "phase43y_startup_snapshot.json");
            var json = JsonSerializer.Serialize(_currentSnapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            _logger.LogInformation("[RIL] Startup snapshot written to {Path}", path);
            _logger.LogInformation("[RIL] Config: hash={Hash}, stack=[{Stack}]",
                _currentSnapshot.ConfigHash, string.Join(",", _currentSnapshot.EnabledStack));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RIL] Failed to write startup snapshot");
        }
    }
}
