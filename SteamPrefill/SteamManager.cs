﻿namespace SteamPrefill
{
    public sealed class SteamManager : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;

        private readonly Steam3Session _steam3;
        private readonly CdnPool _cdnPool;

        private readonly DownloadHandler _downloadHandler;
        private readonly DepotHandler _depotHandler;
        private readonly AppInfoHandler _appInfoHandler;

        private PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public SteamManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;

#if DEBUG
            if (AppConfig.EnableSteamKitDebugLogs)
            {
                DebugLog.AddListener(new SteamKitDebugListener(_ansiConsole));
                DebugLog.Enabled = true;
            }
#endif

            _steam3 = new Steam3Session(_ansiConsole);
            _cdnPool = new CdnPool(_ansiConsole, _steam3);
            _appInfoHandler = new AppInfoHandler(_ansiConsole, _steam3);
            _downloadHandler = new DownloadHandler(_ansiConsole, _cdnPool);
            _depotHandler = new DepotHandler(_ansiConsole, _steam3, _appInfoHandler, _cdnPool, downloadArgs);
        }

        #region Startup + Shutdown

        /// <summary>
        /// Logs the user into the Steam network, and retrieves available CDN servers and account licenses.
        ///
        /// Required to be called first before using SteamManager class.
        /// </summary>
        public async Task InitializeAsync()
        {
            var timer = Stopwatch.StartNew();
            _ansiConsole.LogMarkupLine("Starting login!");

            await _steam3.LoginToSteamAsync();
            _steam3.WaitForLicenseCallback();

#if DEBUG
            _ansiConsole.LogMarkupLine("Steam session initialization complete!", timer);
#else
            _ansiConsole.LogMarkupLine("Steam session initialization complete!");
#endif

        }

        public void Shutdown()
        {
            _steam3.Disconnect();
        }

        public void Dispose()
        {
            _downloadHandler.Dispose();
            _steam3.Dispose();
        }

        #endregion

        #region Prefill

        public async Task DownloadMultipleAppsAsync(bool downloadAllOwnedGames, bool prefillRecentGames, int? prefillPopularGames, List<uint> manualIds)
        {
            var appIdsToDownload = LoadPreviouslySelectedApps();
            appIdsToDownload.AddRange(manualIds);
            if (downloadAllOwnedGames)
            {
                appIdsToDownload.AddRange(_steam3.OwnedAppIds);
            }
            if (prefillRecentGames)
            {
                var recentGames = await _appInfoHandler.GetRecentlyPlayedGamesAsync();
                appIdsToDownload.AddRange(recentGames.Select(e => (uint)e.appid));
            }
            if (prefillPopularGames != null)
            {
                var popularGames = (await SteamSpy.TopGamesLast2WeeksAsync(_ansiConsole))
                                    .Take(prefillPopularGames.Value)
                                    .Select(e => e.appid);
                appIdsToDownload.AddRange(popularGames);
            }

            var distinctAppIds = appIdsToDownload.Distinct().ToList();
            await _appInfoHandler.RetrieveAppMetadataAsync(distinctAppIds);

            // Whitespace divider
            _ansiConsole.WriteLine();

            var availableGames = await _appInfoHandler.GetGamesByIdAsync(distinctAppIds);
            foreach (var app in availableGames)
            {
                try
                {
                    await DownloadSingleAppAsync(app.AppId);
                }
                catch (Exception e) when (e is LancacheNotFoundException || e is UserCancelledException || e is InfiniteLoopException)
                {
                    // We'll want to bomb out the entire process for these exceptions, as they mean we can't prefill any apps at all
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    _ansiConsole.MarkupLine(Red($"   Unexpected download error : {e.Message}"));
                    _ansiConsole.MarkupLine("");
                    _prefillSummaryResult.FailedApps++;
                }
            }
            await PrintUnownedAppsAsync(distinctAppIds);

            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole, availableGames.Count);
        }

        private async Task DownloadSingleAppAsync(uint appId)
        {
            AppInfo appInfo = await _appInfoHandler.GetAppInfoAsync(appId);

            // Filter depots based on specified lang/os/architecture/etc
            var filteredDepots = await _depotHandler.FilterDepotsToDownloadAsync(_downloadArgs, appInfo.Depots);
            if (!filteredDepots.Any())
            {
                //TODO add to summary output?
                _ansiConsole.LogMarkupLine($"Starting {Cyan(appInfo)}  {LightYellow("No depots to download.  Current arguments filtered all depots")}");
                return;
            }

            await _depotHandler.BuildLinkedDepotInfoAsync(filteredDepots);

            // We will want to re-download the entire app, if any of the depots have been updated
            if (_downloadArgs.Force == false && _depotHandler.AppIsUpToDate(filteredDepots))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                if (!AppConfig.VerboseLogs)
                {
                    return;
                }

                _ansiConsole.LogMarkupLine($"Starting {Cyan(appInfo)}  {Green("  Up to date!")}");
                return;
            }

            _ansiConsole.LogMarkupLine($"Starting {Cyan(appInfo)}");

            //TODO is this needed anymore?
            await _cdnPool.PopulateAvailableServersAsync();

            // Get the full file list for each depot, and queue up the required chunks
            //TODO not a fan of having to do the status spinner here instead of inside the manifest handler
            List<QueuedRequest> chunkDownloadQueue = null;
            await _ansiConsole.StatusSpinner().StartAsync("Fetching depot manifests...", async _ =>
            {
                chunkDownloadQueue = await _depotHandler.BuildChunkDownloadQueueAsync(filteredDepots);
            });

            // Finally run the queued downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => e.CompressedLength));

            _ansiConsole.LogMarkup($"Downloading {Magenta(totalBytes.ToDecimalString())}");
#if DEBUG
            _ansiConsole.Markup($" from {LightYellow(chunkDownloadQueue.Count)} chunks");
#endif
            _ansiConsole.MarkupLine("");

            var downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, _downloadArgs);
            if (downloadSuccessful)
            {
                _depotHandler.MarkDownloadAsSuccessful(filteredDepots);
                _prefillSummaryResult.Updated++;
            }
            downloadTimer.Stop();

            // Logging some metrics about the download
            _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
            _ansiConsole.WriteLine();
        }

        #endregion

        #region Select Apps

        public void SetAppsAsSelected(List<AppInfo> userSelected)
        {
            List<uint> selectedAppIds = userSelected
                                        .Select(e => e.AppId)
                                        .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListUInt32));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!  ");
        }

        public List<uint> LoadPreviouslySelectedApps()
        {
            if (File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListUInt32);
            }
            return new List<uint>();
        }

        #endregion

        public async Task<List<AppInfo>> GetAllAvailableGamesAsync()
        {
            var ownedGameIds = _steam3.OwnedAppIds.ToList();

            // Loading app metadata from steam, skipping related DLC apps
            await _appInfoHandler.RetrieveAppMetadataAsync(ownedGameIds, loadDlcApps: false, loadRecentlyPlayed: true);
            var availableGames = await _appInfoHandler.GetGamesByIdAsync(ownedGameIds);
            return availableGames;
        }

        private async Task PrintUnownedAppsAsync(List<uint> distinctAppIds)
        {
            // Write out any apps that can't be downloaded as a warning message, so users can know that they were skipped
            AppInfo[] unownedApps = await Task.WhenAll(distinctAppIds.Where(e => !_steam3.AccountHasAppAccess(e))
                                                                      .Select(e => _appInfoHandler.GetAppInfoAsync(e)));
            _prefillSummaryResult.UnownedAppsSkipped = unownedApps.Length;


            if (!unownedApps.Any())
            {
                return;
            }

            var table = new Table { Border = TableBorder.MinimalHeavyHead };
            // Header
            table.AddColumn(new TableColumn(White("App")));

            // Rows
            foreach (var app in unownedApps.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow($"[link=https://store.steampowered.com/app/{app.AppId}]🔗[/] {White(app.Name)}");
            }

            _ansiConsole.MarkupLine("");
            _ansiConsole.MarkupLine(LightYellow($" Warning!  Found {Magenta(unownedApps.Length)} unowned apps!  They will be excluded from this prefill run..."));
            _ansiConsole.Write(table);
        }

        #region Benchmarking
        
        public async Task SetupBenchmarkAsync(List<uint> appIds, bool useAllOwnedGames, bool useSelectedApps)
        {
            _ansiConsole.WriteLine();
            _ansiConsole.LogMarkupLine("Building benchmark workload file...");

            // Building out list of apps to benchmark
            if (useSelectedApps)
            {
                appIds.AddRange(LoadPreviouslySelectedApps());
            }
            if (useAllOwnedGames)
            {
                appIds.AddRange(_steam3.OwnedAppIds);
            }
            appIds = appIds.Distinct().ToList();

            // Preloading as much metadata as possible
            await _appInfoHandler.RetrieveAppMetadataAsync(appIds);
            await PrintUnownedAppsAsync(appIds);

            // Building out the combined workload file
            var benchmarkWorkload = await BuildBenchmarkWorkloadAsync(appIds);

            // Saving results to disk
            benchmarkWorkload.SaveToFile(AppConfig.BenchmarkWorkloadPath);

            // Writing stats
            benchmarkWorkload.PrintSummary(_ansiConsole);

            var fileSize = ByteSize.FromBytes(new FileInfo(AppConfig.BenchmarkWorkloadPath).Length);
            _ansiConsole.Write(new Rule());
            _ansiConsole.LogMarkupLine("Completed build of workload file...");
            _ansiConsole.LogMarkupLine($"Resulting file size : {MediumPurple(fileSize.ToBinaryString())}");
        }

        //TODO document
        private async Task<BenchmarkWorkload> BuildBenchmarkWorkloadAsync(List<uint> appIds)
        {
            await _cdnPool.PopulateAvailableServersAsync();

            var benchmarkFileList = new BenchmarkWorkload();
            await _ansiConsole.CreateSpectreProgress(TransferSpeedUnit.Bytes, displayTransferRate: false).StartAsync(async ctx =>
            {
                var gamesToUse = await _appInfoHandler.GetGamesByIdAsync(appIds);
                var overallProgressTask = ctx.AddTask("Processing games..".PadLeft(30), new ProgressTaskSettings { MaxValue = gamesToUse.Count });

                //TODO add a retry loop + handle errors
                //TODO figure out what happens if there are less than 5 cdns to use
                await Parallel.ForEachAsync(gamesToUse, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (appInfo, _) =>
                {
                    var individualProgressTask = ctx.AddTask($"{Cyan(appInfo.Name.Truncate(30).PadLeft(30))}");
                    individualProgressTask.IsIndeterminate = true;

                    try
                    {
                        var filteredDepots = await _depotHandler.FilterDepotsToDownloadAsync(_downloadArgs, appInfo.Depots);
                        await _depotHandler.BuildLinkedDepotInfoAsync(filteredDepots);
                        if (!filteredDepots.Any())
                        {
                            _ansiConsole.LogMarkupLine($"{Cyan(appInfo)} - {LightYellow("No depots to download.  Current arguments filtered all depots")}");
                            return;
                        }

                        // Get the full file list for each depot, and queue up the required chunks
                        var allChunksForApp = await _depotHandler.BuildChunkDownloadQueueAsync(filteredDepots);
                        var appFileListing = new AppQueuedRequests(appInfo.Name, appInfo.AppId, allChunksForApp);
                        benchmarkFileList.QueuedAppsList.Add(appFileListing);
                    }
                    catch (Exception e) when (e is LancacheNotFoundException || e is UserCancelledException || e is InfiniteLoopException)
                    {
                        // Bomb out the whole process, since these are completely unrecoverable
                        throw;
                    }
                    catch (Exception e)
                    {
                        // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                        _ansiConsole.MarkupLine(Red($"   Unexpected error : {e.Message}"));
                        _ansiConsole.MarkupLine("");
                    }

                    benchmarkFileList.CdnServerList = _cdnPool._availableServerEndpoints;

                    overallProgressTask.Increment(1);
                    individualProgressTask.StopTask();
                });
            });
            return benchmarkFileList;
        }

        #endregion
    }
}