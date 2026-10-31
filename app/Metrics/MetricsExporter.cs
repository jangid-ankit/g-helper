using GHelper.Helpers;
using System.Globalization;
using System.Text;

namespace GHelper.Metrics
{
    /// <summary>
    /// Records hardware sensor metrics to a timestamped CSV file at 1-second intervals.
    /// Recording survives settings-panel close; it stops only when explicitly toggled off
    /// or when the application exits.
    ///
    /// Output location: Documents\GHelper\metrics_YYYY-MM-DD_HH-mm-ss.csv
    ///
    /// Time columns:
    ///   Elapsed_s   – seconds since recording started (0.000, 1.003, 2.001 …).
    ///                 Use this as the X-axis in Excel charts.
    ///   WallClock   – absolute system-clock time at each sample, kept for reference
    ///                 so you know when in the day the session occurred.
    /// </summary>
    public static class MetricsExporter
    {
        // ── Public state ────────────────────────────────────────────────────────────

        /// <summary>True while a recording session is active.</summary>
        public static bool IsRecording => _recordTimer is not null;

        /// <summary>
        /// Raised on the UI thread when recording starts or stops, so the button
        /// can update its appearance without polling.
        /// </summary>
        public static event Action<bool>? RecordingStateChanged;

        // ── Private state ────────────────────────────────────────────────────────────

        private static System.Threading.Timer? _recordTimer;
        private static StreamWriter? _writer;
        private static string? _currentFilePath;

        /// <summary>
        /// The exact moment StartRecording() was called.
        /// All Elapsed_s values are (now - _sessionStart).TotalSeconds.
        /// </summary>
        private static DateTimeOffset _sessionStart;

        private static readonly object _lock = new();

        // ── CSV column definitions ────────────────────────────────────────────────────

        private const string CsvHeader =
            "Elapsed_s," +
            "WallClock," +
            "CPU_Temp_C," +
            "GPU_Temp_C," +
            "CPU_Fan_RPM," +
            "GPU_Fan_RPM," +
            "CPU_Load_Pct," +
            "GPU_Load_Pct," +
            "CPU_Power_W," +
            "GPU_Power_W," +
            "RAM_Used_MB," +
            "RAM_Load_Pct," +
            "VRAM_Used_MB," +
            "Battery_Rate_W," +
            "Battery_Pct";

        // ── Public API ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a new recording session. Creates a fresh CSV file, writes a
        /// metadata comment with the session start time, then begins writing a
        /// row every second. No-op if already recording.
        /// </summary>
        public static void StartRecording()
        {
            lock (_lock)
            {
                if (IsRecording) return;

                try
                {
                    _sessionStart = DateTimeOffset.Now;
                    _currentFilePath = BuildFilePath(_sessionStart);
                    Directory.CreateDirectory(Path.GetDirectoryName(_currentFilePath)!);

                    // Keep file open for the duration; UTF-8 without BOM, auto-flush every row.
                    _writer = new StreamWriter(_currentFilePath, append: false, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };

                    // Human-readable session header so the file is self-documenting.
                    _writer.WriteLine(
                        $"# GHelper metrics — session started {_sessionStart:yyyy-MM-dd HH:mm:ss zzz}");
                    _writer.WriteLine(
                        "# Elapsed_s is seconds since session start (use as X-axis). " +
                        "WallClock is absolute local time for reference.");
                    _writer.WriteLine(CsvHeader);

                    // Fire immediately (dueTime=0) then every 1 000 ms.
                    _recordTimer = new System.Threading.Timer(
                        OnTick, null,
                        dueTime: 0,
                        period: 1000);

                    Logger.WriteLine($"[MetricsExporter] Recording started → {_currentFilePath}");
                    NotifyStateChanged(recording: true);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[MetricsExporter] Failed to start: {ex.Message}");
                    Cleanup();
                }
            }
        }

        /// <summary>
        /// Stops the current recording session and flushes + closes the CSV file.
        /// No-op if not recording.
        /// </summary>
        public static void StopRecording()
        {
            lock (_lock)
            {
                if (!IsRecording) return;
                Cleanup();
                Logger.WriteLine($"[MetricsExporter] Recording stopped → {_currentFilePath}");
                NotifyStateChanged(recording: false);
            }
        }

        /// <summary>Toggles recording on/off. Returns the new state (true = now recording).</summary>
        public static bool Toggle()
        {
            if (IsRecording) { StopRecording(); return false; }
            StartRecording(); return true;
        }

        /// <summary>
        /// Returns the path to the file from the most recent (or active) session,
        /// or null if recording has never been started.
        /// </summary>
        public static string? CurrentFilePath => _currentFilePath;

        // ── Timer callback ────────────────────────────────────────────────────────────

        private static void OnTick(object? _)
        {
            lock (_lock)
            {
                if (_writer is null) return;

                try
                {
                    _writer.WriteLine(BuildRow());
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[MetricsExporter] Write error: {ex.Message}");
                    Cleanup();
                    NotifyStateChanged(recording: false);
                }
            }
        }

        // ── Row builder ────────────────────────────────────────────────────────────────

        private static string BuildRow()
        {
            // Capture wall-clock once so both time columns reflect the same instant.
            DateTimeOffset now = DateTimeOffset.Now;

            // Elapsed_s: seconds since the session started, 3 decimal places (ms precision).
            string elapsedS  = (now - _sessionStart).TotalSeconds
                                   .ToString("F3", CultureInfo.InvariantCulture);

            // WallClock: ISO-like local time, kept as reference only (not for charting).
            string wallClock = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            // Snapshot hardware fields — simple value-type reads, no extra locking needed.
            string cpuTemp   = FormatF(HardwareControl.cpuTemp);
            string gpuTemp   = FormatF(HardwareControl.gpuTemp);
            string cpuFanRpm = FormatI(HardwareControl.cpuFanRPM);
            string gpuFanRpm = FormatI(HardwareControl.gpuFanRPM);
            string cpuLoad   = FormatI(HardwareControl.cpuUsage);
            string gpuLoad   = FormatI(HardwareControl.gpuUsage);
            string cpuPower  = FormatF(HardwareControl.cpuPower);
            string gpuPower  = FormatF(HardwareControl.gpuPower);
            string ramMb     = FormatI(HardwareControl.ramUsedMb);
            string ramPct    = FormatI(HardwareControl.ramUsage);
            string vramMb    = FormatI(HardwareControl.vramUsedMb);
            string battRate  = FormatD(HardwareControl.batteryRate);
            string battPct   = HardwareControl.batteryCapacity >= 0
                                   ? Math.Round(HardwareControl.batteryCapacity, 1)
                                         .ToString(CultureInfo.InvariantCulture)
                                   : string.Empty;

            return string.Join(",",
                elapsedS, wallClock,
                cpuTemp, gpuTemp,
                cpuFanRpm, gpuFanRpm,
                cpuLoad, gpuLoad,
                cpuPower, gpuPower,
                ramMb, ramPct, vramMb,
                battRate, battPct);
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────

        private static string BuildFilePath(DateTimeOffset sessionStart)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "GHelper");
            string name = $"metrics_{sessionStart:yyyy-MM-dd_HH-mm-ss}.csv";
            return Path.Combine(dir, name);
        }

        private static void Cleanup()
        {
            _recordTimer?.Dispose();
            _recordTimer = null;

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }

        private static void NotifyStateChanged(bool recording)
        {
            // Marshal to the UI thread if a form handle exists.
            try
            {
                if (Program.settingsForm?.IsHandleCreated == true)
                    Program.settingsForm.BeginInvoke(() => RecordingStateChanged?.Invoke(recording));
                else
                    RecordingStateChanged?.Invoke(recording);
            }
            catch { /* form disposed during shutdown */ }
        }

        // ── Null-safe formatters (empty string = missing data in CSV) ─────────────────

        private static string FormatF(float? v)  => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;
        private static string FormatI(int? v)     => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        private static string FormatD(decimal? v) => v.HasValue ? Math.Round(v.Value, 2).ToString(CultureInfo.InvariantCulture) : string.Empty;
    }
}
