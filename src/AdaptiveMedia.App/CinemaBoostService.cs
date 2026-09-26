using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AdaptiveMedia;

/// <summary>Starts a separate Windows PowerShell watchdog that owns one temporary
/// WMI brightness change. Closing its stdin ends the session; parent death also
/// closes the pipe and is independently checked by PID and start time.</summary>
public static class CinemaBoostService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static CinemaBoostSession? TryStart(DisplayCapability? planned, bool userEnabled)
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var power)) return null;
        IReadOnlyList<DisplayCapability> current;
        try { current = DisplayCapabilityProbe.Read(); }
        catch { return null; }
        if (!CinemaBoostPolicy.CanStart(userEnabled, planned, current,
            power.AcLineStatus == 1, power.SystemStatusFlag != 0)) return null;

        using var parent = Process.GetCurrentProcess();
        var script = WatchdogScript.Replace("__PNP__", PsQuote(planned!.PnpId))
            .Replace("__EDID__", PsQuote(planned.EdidFingerprint!))
            .Replace("__PARENT_ID__", parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__PARENT_START__", parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) return null;
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process? watchdog = null;
        try
        {
            watchdog = Process.Start(start);
            if (watchdog is null) return null;
            var ready = watchdog.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))
                .GetAwaiter().GetResult();
            var evidence = CinemaBoostEvidence.TryParse(ready);
            if (evidence is not null) return new CinemaBoostSession(watchdog, evidence, planned.PnpId, planned.EdidFingerprint!);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { /* Failure to establish an independent watchdog means no session. */ }
        if (watchdog is not null)
        {
            try { watchdog.StandardInput.Close(); } catch (IOException) { }
            // A slow WMI call may still complete; leave the helper alive so its
            // finally block can restore rather than killing it after timeout.
            watchdog.Dispose();
        }
        return null;
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private const string WatchdogScript = """
        $ErrorActionPreference = 'Stop'
        $pnp = __PNP__
        $edid = __EDID__
        $parentId = __PARENT_ID__
        $parentStart = __PARENT_START__
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class CinemaPower {
            [StructLayout(LayoutKind.Sequential)]
            public struct Status { public byte Ac, Battery, Percent, Saver; public uint Life, FullLife; }
            [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out Status status);
        }
        '@
        function OnAcWithoutSaver {
            $status = New-Object CinemaPower+Status
            return [CinemaPower]::GetSystemPowerStatus([ref]$status) -and $status.Ac -eq 1 -and $status.Saver -eq 0
        }
        function ExactPanel {
            # DisplayConfig and WMI may expose different PnP instance paths for
            # one panel. Both keys must have the captured full EDID fingerprint.
            $plannedKey = Get-ItemProperty -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Enum\" + $pnp + "\Device Parameters") -Name EDID
            $plannedHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([byte[]]$plannedKey.EDID)).Replace('-', '')
            if ($plannedHash -ne $edid) { return $null }
            $items = @(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness | Where-Object Active | ForEach-Object {
                if ($_.InstanceName -notmatch '_[0-9]+$') { return }
                $wmiPnp = $_.InstanceName.Substring(0, $_.InstanceName.LastIndexOf('_'))
                $wmiKey = Get-ItemProperty -LiteralPath ("HKLM:\SYSTEM\CurrentControlSet\Enum\" + $wmiPnp + "\Device Parameters") -Name EDID
                $wmiHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([byte[]]$wmiKey.EDID)).Replace('-', '')
                if ($wmiHash -eq $edid) { $_ }
            })
            if ($items.Count -ne 1) { return $null }
            if ($null -ne $script:lockedInstance -and $items[0].InstanceName -ne $script:lockedInstance) { return $null }
            return $items[0]
        }
        function ExactMethod($instance) {
            $items = @(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightnessMethods | Where-Object { $_.InstanceName -eq $instance })
            if ($items.Count -ne 1) { return $null }
            return $items[0]
        }
        $boosted = $false
        $original = -1
        $applied = -1
        $script:lockedInstance = $null
        try {
            if (-not (OnAcWithoutSaver)) { [Console]::Out.WriteLine('SKIP'); return }
            $panel = ExactPanel
            if ($null -eq $panel) { [Console]::Out.WriteLine('SKIP'); return }
            $script:lockedInstance = $panel.InstanceName
            $method = ExactMethod $panel.InstanceName
            if ($null -eq $method) { [Console]::Out.WriteLine('SKIP'); return }
            $original = [int]$panel.CurrentBrightness
            if ($original -ge 100 -or $original -lt 0) { [Console]::Out.WriteLine('SKIP'); return }
            $result = Invoke-CimMethod -InputObject $method -MethodName WmiSetBrightness -Arguments @{ Timeout = 0; Brightness = [byte]100 }
            $boosted = $true
            Start-Sleep -Milliseconds 500
            $panel = ExactPanel
            if ($null -eq $panel) { [Console]::Out.WriteLine('SKIP'); return }
            $applied = [int]$panel.CurrentBrightness
            if (($null -ne $result.ReturnValue -and $result.ReturnValue -ne 0) -or $applied -ne 100) { [Console]::Out.WriteLine('SKIP'); return }
            [Console]::Out.WriteLine(('BOOSTED|{0}|{1}|{2}' -f $original, [int]$panel.CurrentBrightness, $panel.InstanceName))
            $inputTask = [Console]::In.ReadLineAsync()
            while ($true) {
                if ($inputTask.IsCompleted) { break }
                $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
                if ($null -eq $parent -or $parent.StartTime.ToUniversalTime().Ticks -ne $parentStart) { break }
                if (-not (OnAcWithoutSaver)) { break }
                $panel = ExactPanel
                if ($null -eq $panel -or [int]$panel.CurrentBrightness -ne 100) { break }
                Start-Sleep -Seconds 1
            }
        } catch {
            [Console]::Out.WriteLine('ERROR')
        } finally {
            if ($boosted) {
                try {
                    $panel = ExactPanel
                    if ($null -ne $panel -and $applied -ge 0 -and $applied -ne $original -and [int]$panel.CurrentBrightness -eq $applied) {
                        $method = ExactMethod $panel.InstanceName
                        if ($null -ne $method) {
                            $null = Invoke-CimMethod -InputObject $method -MethodName WmiSetBrightness -Arguments @{ Timeout = 0; Brightness = [byte]$original }
                        }
                    }
                } catch { }
            }
        }
        """;
}

public sealed class CinemaBoostSession : IDisposable
{
    private Process? _watchdog;
    public int OriginalBrightnessPercent { get; }
    public int ObservedBoostedPercent { get; }
    public string DisplayPnpId { get; }
    public string EdidFingerprint { get; }
    public string WmiInstanceName { get; }
    public bool IsActive => _watchdog is { HasExited: false };
    internal CinemaBoostSession(Process watchdog, CinemaBoostEvidence evidence, string displayPnpId, string edidFingerprint)
    {
        _watchdog = watchdog;
        OriginalBrightnessPercent = evidence.OriginalBrightnessPercent;
        ObservedBoostedPercent = evidence.ObservedBoostedPercent;
        WmiInstanceName = evidence.WmiInstanceName;
        DisplayPnpId = displayPnpId;
        EdidFingerprint = edidFingerprint;
    }
    public void Dispose()
    {
        var watchdog = Interlocked.Exchange(ref _watchdog, null);
        if (watchdog is null) return;
        try { watchdog.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
        try { watchdog.WaitForExit(5000); } catch (InvalidOperationException) { }
        watchdog.Dispose();
    }
}
