using AdaptiveMedia;

int count = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; }
var panel = new DisplayCapability(SourceName: "\\\\.\\DISPLAY1", PnpId: "DISPLAY\\ABC123\\4&XYZ",
    AdapterLow: 42, SourceId: 3, TargetId: 7, OutputTechnology: 0x80000000, EdidFingerprint: new string('A', 64),
    WcgSupported: true, WcgActive: true, AdvancedColorActive: true, ActiveColorMode: "WCG",
    BitsPerColor: 10, DxgiColorSpace: 0, ReportedMaxLuminance: 500f,
    RedPrimary: [0.68f, 0.32f], GreenPrimary: [0.265f, 0.69f], BluePrimary: [0.15f, 0.06f]);
var current = new[] { panel };
Check(!CinemaBoostPolicy.CanStart(false, panel, current, true, false), "opt-in required");
Check(!CinemaBoostPolicy.CanStart(true, panel, current, false, false), "AC required");
Check(!CinemaBoostPolicy.CanStart(true, panel, current, true, true), "Energy Saver disables boost");
Check(CinemaBoostPolicy.CanStart(true, panel, current, true, false), "exact active panel can boost");
Check(!CinemaBoostPolicy.CanStart(true, panel with { WcgActive = false }, current, true, false), "unqualified planned WCG path rejected");
Check(!CinemaBoostPolicy.CanStart(true, panel, [panel with { WcgActive = false }], true, false), "WCG deactivated after planning is rejected");
Check(!CinemaBoostPolicy.CanStart(true, panel, [panel with { TargetId = 8 }], true, false), "changed target rejected");
Check(!CinemaBoostPolicy.CanStart(true, panel, [panel with { EdidFingerprint = new string('B', 64) }], true, false), "changed EDID rejected");
Check(!CinemaBoostPolicy.CanStart(true, panel, [panel, panel], true, false), "ambiguous active panel rejected");
Check(!CinemaBoostPolicy.CanStart(true, panel with { EdidFingerprint = null }, current, true, false), "unidentified panel rejected");
Check(CinemaBoostPolicy.ShouldRestore(30, 100, 100, true), "unchanged boost restores original");
Check(CinemaBoostPolicy.ShouldRestore(80, 99, 99, true), "partial set readback restores original");
Check(!CinemaBoostPolicy.ShouldRestore(80, 99, 90, true), "user change after partial set wins");
Check(!CinemaBoostPolicy.ShouldRestore(30, 100, 65, true), "user brightness change wins");
Check(!CinemaBoostPolicy.ShouldRestore(30, 100, 100, false), "panel identity change blocks restore");
var data = Path.Combine(Path.GetTempPath(), "CinemaBoostTests-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", data);
try {
    Check(!SettingsStore.Load().CinemaBoost, "boost defaults off");
    Check(SettingsStore.Save(new AppSettings { CinemaBoost = true }) && SettingsStore.Load().CinemaBoost, "explicit boost opt-in persists");
} finally { Environment.SetEnvironmentVariable("ADAPTIVE_MEDIA_DATA_DIR", null); if (Directory.Exists(data)) Directory.Delete(data, true); }
Check(CinemaBoostEvidence.TryParse("BOOSTED|80|100|DISPLAY\\BOE0C4B\\INSTANCE_0") is { OriginalBrightnessPercent: 80, ObservedBoostedPercent: 100 }, "watchdog reports observed brightness transition");
Check(CinemaBoostEvidence.TryParse("BOOSTED|80|99|DISPLAY\\BOE0C4B\\INSTANCE_0") is null, "partial boost is not reported as established");
Check(CinemaBoostEvidence.TryParse("SKIP") is null, "skipped boost has no evidence");
Console.WriteLine($"Cinema Boost tests: {count} PASS");

