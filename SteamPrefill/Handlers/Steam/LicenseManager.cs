﻿using SteamKit2;
using static SteamKit2.SteamApps;

namespace SteamPrefill.Handlers.Steam
{
    public sealed class LicenseManager
    {
        private readonly SteamApps _steamAppsApi;
        private readonly UserAccountStore _userAccountStore;

        private string LicensesPath => Path.Combine(AppConfig.CacheDir, $"userLicenses_{_userAccountStore.CurrentUsername}.json");

        internal UserLicenses _userLicenses = new UserLicenses();

        public List<uint> AllOwnedAppIds => _userLicenses.OwnedAppIds.ToList();

        public LicenseManager(SteamApps steamAppsApi, UserAccountStore userAccountStore)
        {
            _steamAppsApi = steamAppsApi;
            _userAccountStore = userAccountStore;
        }

        /// <summary>
        /// Checks against the list of currently owned apps to determine if the user is able to download this app.
        /// </summary>
        /// <param name="appid">Id of the application to check for access</param>
        /// <returns>True if the user has access to the app</returns>
        public bool AccountHasAppAccess(uint appid)
        {
            return _userLicenses.OwnedAppIds.Contains(appid);
        }

        /// <summary>
        /// Checks against the list of currently owned apps to determine if the user is able to download this depot.
        /// </summary>
        /// <param name="depotId">Id of the depot to check for access</param>
        /// <returns>True if the user has access to the depot</returns>
        public bool AccountHasDepotAccess(uint depotId)
        {
            return _userLicenses.OwnedDepotIds.Contains(depotId);
        }
        
        public void LoadPackageInfo(IReadOnlyCollection<LicenseListCallback.License> licenseList)
        {
            // Filters out licenses that are subscription based, and have expired, like EA Play for example.
            // The account will continue to "own" the packages, and will be unable to download their apps, so they must be filtered out here.
            var nonExpiredLicenses = licenseList.Where(e => !e.LicenseFlags.HasFlag(ELicenseFlags.Expired)).ToList();

            // If we haven't bought any new games (or free-to-play) since the last run, we can reload our owned Apps/Depots
            if (File.Exists(LicensesPath))
            {
                var deserialized = JsonSerializer.Deserialize(File.ReadAllText(LicensesPath), SerializationContext.Default.UserLicenses);
                if (deserialized.LicenseCount == nonExpiredLicenses.Count)
                {
                    _userLicenses = deserialized;
                    return;
                }
            }
            _userLicenses = new UserLicenses();

            // Some packages require a access token in order to request their apps/depot list
            var packageRequests = nonExpiredLicenses.Select(e => new PICSRequest(e.PackageID, e.AccessToken)).ToList();

            var jobResult = _steamAppsApi.PICSGetProductInfo(new List<PICSRequest>(), packageRequests).ToTask().Result;
            var packageInfos = jobResult.Results.SelectMany(e => e.Packages)
                                        .Select(e => e.Value)
                                        .Select(e => new Package(e.KeyValues))
                                        .OrderBy(e => e.Id)
                                        .ToList();

            _userLicenses.LicenseCount = nonExpiredLicenses.Count;
            _userLicenses.OwnedPackageIds.AddRange(packageInfos.Select(e => e.Id).ToList());

            // Handling packages that are normally purchased or added via cd-key
            foreach (var package in packageInfos)
            {
                // Removing any free weekends that are no longer active
                if (package.IsFreeWeekend && package.FreeWeekendHasExpired)
                { 
                    continue;
                }
                
                _userLicenses.OwnedAppIds.AddRange(package.AppIds);
                _userLicenses.OwnedDepotIds.AddRange(package.DepotIds);
            }

            // Serializing this data to speedup subsequent runs
            File.WriteAllText(LicensesPath, JsonSerializer.Serialize(_userLicenses, SerializationContext.Default.UserLicenses));
        }
    }

    public sealed class UserLicenses
    {
        public int LicenseCount { get; set; }

        public HashSet<uint> OwnedPackageIds { get; set; } = new HashSet<uint>();
        public HashSet<uint> OwnedAppIds { get; set; } = new HashSet<uint>();
        public HashSet<uint> OwnedDepotIds { get; set; } = new HashSet<uint>();

        public override string ToString()
        {
            return $"Packages : {OwnedPackageIds.Count} Apps : {OwnedAppIds.Count} Depots : {OwnedDepotIds.Count}";
        }
    }
}