namespace SteamPrefill.Settings
{
    public static class AppConfig
    {
        static AppConfig()
        {
            // Create required folders
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(TempDir);
        }

        /// <summary>
        /// This domain is used to determine if there is an available Lancache instance available.
        /// Resolving a private IP indicates that there is a cache available.
        /// This is the same domain that the real Steam client uses to determine if a cache is available.
        /// </summary>
        public static string SteamTriggerDomain => "lancache.steamcontent.com";

        private static bool _verboseLogs;
        public static bool VerboseLogs
        {
            get => _verboseLogs;
            set
            {
                _verboseLogs = value;
                AnsiConsoleExtensions.WriteVerboseLogs = value;
            }
        }

        /// <summary>
        /// Downloaded manifests, as well as other metadata, are saved into this directory to speedup future prefill runs.
        /// All data in here should be able to be deleted safely.
        /// </summary>
        public static readonly string TempDir = TempDirUtils.GetTempDirBaseDirectories("SteamPrefill", "v1");

        /// <summary>
        /// Contains user configuration.  Should not be deleted, doing so will reset the app back to defaults.
        /// </summary>
        private static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

        #region Serialization file paths

        public static readonly string AccountSettingsStorePath = Path.Combine(ConfigDir, "account.config");

        /// <summary>
        /// Generated by the 'benchmark setup' command, is portable and can be moved with the app.
        /// </summary>
        public static readonly string BenchmarkWorkloadPath = Path.Combine(ConfigDir, "benchmarkWorkload.bin");
        public static readonly string UserSelectedAppsPath = Path.Combine(ConfigDir, "selectedAppsToPrefill.json");

        /// <summary>
        /// Keeps track of which depots have been previously downloaded.  Is used to determine whether a game is up-to-date,
        /// based on whether all the depots being downloaded are up-to-date.
        /// </summary>
        public static readonly string SuccessfullyDownloadedDepotsPath = Path.Combine(ConfigDir, "successfullyDownloadedDepots.json");

        /// <summary>
        /// Stores the user's current CellId, which corresponds to their region.
        /// </summary>
        /// <see cref="Steam3Session.CellId">See for additional documentation</see>
        public static readonly string CachedCellIdPath = Path.Combine(TempDir, "cellId.txt");

        #endregion

        #region Debugging

        public static readonly string DebugOutputDir = Path.Combine(TempDir, "Debugging");

        /// <summary>
        /// Skips using locally cached manifests. Saves disk space, at the expense of slower subsequent runs.  Intended for debugging.
        /// </summary>
        public static bool NoLocalCache { get; set; }

        /// <summary>
        /// Will skip over downloading chunks, but will still download manifests and build the chunk download list.  Useful for testing
        /// core logic of SteamPrefill without having to wait for downloads to finish.
        /// </summary>
        public static bool SkipDownloads { get; set; }

        private static bool _debugLogs;
        public static bool DebugLogs
        {
            get => _debugLogs;
            set
            {
                _debugLogs = value;

                // Enable verbose logs as well
                VerboseLogs = true;
            }
        }

        public static uint? CellIdOverride { get; set; }

        #endregion
    }
}