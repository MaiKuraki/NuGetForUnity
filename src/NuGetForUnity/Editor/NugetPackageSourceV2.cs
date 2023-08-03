﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NugetForUnity
{
    /// <summary>
    ///     Represents a NuGet Package Source that uses a remote server with API version v2.
    /// </summary>
    [Serializable]
    internal sealed class NugetPackageSourceV2 : INugetPackageSource, ISerializationCallbackReceiver
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="NugetPackageSourceV2" /> class.
        /// </summary>
        /// <param name="name">The name of the package source.</param>
        /// <param name="url">The path to the package source.</param>
        public NugetPackageSourceV2(string name, string url)
        {
            Name = name;
            SavedPath = url;
            IsEnabled = true;
        }

        /// <inheritdoc />
        [field: SerializeField]
        public string Name { get; set; }

        /// <inheritdoc />
        [field: SerializeField]
        public string SavedPath { get; set; }

        /// <summary>
        ///     Gets path, with the values of environment variables expanded.
        /// </summary>
        public string ExpandedPath
        {
            get
            {
                var path = Environment.ExpandEnvironmentVariables(SavedPath);
                return path;
            }
        }

        /// <inheritdoc />
        [field: SerializeField]
        public string UserName { get; set; }

        /// <inheritdoc />
        [field: SerializeField]
        public string SavedPassword { get; set; }

        /// <summary>
        ///     Gets password, with the values of environment variables expanded.
        /// </summary>
        public string ExpandedPassword => SavedPassword != null ? Environment.ExpandEnvironmentVariables(SavedPassword) : null;

        /// <inheritdoc />
        public bool HasPassword
        {
            get => SavedPassword != null;

            set
            {
                if (value)
                {
                    if (SavedPassword == null)
                    {
                        SavedPassword = string.Empty; // Initialize newly-enabled password to empty string.
                    }
                }
                else
                {
                    SavedPassword = null; // Clear password to null when disabled.
                }
            }
        }

        /// <inheritdoc />
        public bool IsLocalPath => false;

        /// <inheritdoc />
        [field: SerializeField]
        public bool IsEnabled { get; set; }

        /// <inheritdoc />
        public void Dispose()
        {
            // nothing to dispose
        }

        /// <inheritdoc />
        public List<INugetPackage> FindPackagesById(INugetPackageIdentifier package)
        {
            List<INugetPackage> foundPackages;

            // See here: http://www.odata.org/documentation/odata-version-2-0/uri-conventions/
            var url = $"{ExpandedPath}FindPackagesById()?id='{package.Id}'";

            // Are we looking for a specific package?
            if (!package.HasVersionRange)
            {
                url = $"{url}&$filter=Version eq '{package.Version}'";
            }
            else
            {
                // As we can't search for a specific Version we need to fetch all versions and remove the non matching versions.
                // As packages can have so many versions to find the correct version we fetch up to 1000 instead of the default 100.
                url = $"{url}&$top=1000";
            }

            try
            {
                foundPackages = GetPackagesFromUrl(url);
            }
            catch (Exception e)
            {
                foundPackages = new List<INugetPackage>();
                Debug.LogErrorFormat("Unable to retrieve package list from {0}\n{1}", url, e);
            }

            if (foundPackages != null)
            {
                // Return all the packages in the range of versions specified by 'package'.
                foundPackages.RemoveAll(p => !package.InRange(p));
                foundPackages.Sort();
            }

            return foundPackages;
        }

        /// <inheritdoc />
        public INugetPackage GetSpecificPackage(INugetPackageIdentifier package)
        {
            if (package.HasVersionRange)
            {
                // if multiple match we use the lowest version
                return FindPackagesById(package).FirstOrDefault();
            }

            var url = $"{ExpandedPath}Packages(Id='{package.Id}',Version='{package.Version}')";
            try
            {
                return GetPackagesFromUrl(url).First();
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("Unable to retrieve package from {0}\n{1}", url, e);
                return null;
            }
        }

        /// <inheritdoc />
        public Task<List<INugetPackage>> Search(
            string searchTerm = "",
            bool includeAllVersions = false,
            bool includePrerelease = false,
            int numberToGet = 15,
            int numberToSkip = 0,
            CancellationToken cancellationToken = default)
        {
            // See the functions and parameters defined here: https://www.nuget.org/api/v2/$metadata
            // Example URL: "http://www.nuget.org/api/v2/Search()?$filter=IsLatestVersion&$orderby=Id&$skip=0&$top=30&searchTerm='newtonsoft'&targetFramework=''&includePrerelease=false";
            var url = ExpandedPath;

            // call the search method
            url += "Search()?";

            // filter results
            if (!includeAllVersions)
            {
                if (!includePrerelease)
                {
                    url += "$filter=IsLatestVersion&";
                }
                else
                {
                    url += "$filter=IsAbsoluteLatestVersion&";
                }
            }

            // order results
            url += "$orderby=DownloadCount desc&";

            // skip a certain number of entries
            url += $"$skip={numberToSkip}&";

            // show a certain number of entries
            url += $"$top={numberToGet}&";

            // apply the search term
            url += $"searchTerm='{searchTerm}'&";

            // apply the target framework filters
            url += "targetFramework=''&";

            // should we include prerelease packages?
            url += $"includePrerelease={includePrerelease.ToString().ToLower()}";

            try
            {
                return Task.FromResult(GetPackagesFromUrl(url));
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("Unable to retrieve package list from {0}\n{1}", url, e);
                return Task.FromResult(new List<INugetPackage>());
            }
        }

        /// <inheritdoc />
        public List<INugetPackage> GetUpdates(
            IEnumerable<INugetPackage> packages,
            bool includePrerelease = false,
            bool includeAllVersions = false,
            string targetFrameworks = "",
            string versionConstraints = "")
        {
            var updates = new List<INugetPackage>();
            var packagesCollection = packages as ICollection<INugetPackage> ?? packages.ToList();

            // check for updates in groups of 10 instead of all of them, since that causes servers to throw errors for queries that are too long
            for (var i = 0; i < packagesCollection.Count; i += 10)
            {
                var packageGroup = packagesCollection.Skip(i).Take(10);

                var packageIds = string.Empty;
                var versions = string.Empty;

                foreach (var package in packageGroup)
                {
                    if (string.IsNullOrEmpty(packageIds))
                    {
                        packageIds += package.Id;
                    }
                    else
                    {
                        packageIds += "|" + package.Id;
                    }

                    if (string.IsNullOrEmpty(versions))
                    {
                        versions += package.Version;
                    }
                    else
                    {
                        versions += "|" + package.Version;
                    }
                }

                var url =
                    $"{ExpandedPath}GetUpdates()?packageIds='{packageIds}'&versions='{versions}'&includePrerelease={includePrerelease.ToString().ToLower()}&includeAllVersions={includeAllVersions.ToString().ToLower()}&targetFrameworks='{targetFrameworks}'&versionConstraints='{versionConstraints}'";

                try
                {
                    var newPackages = GetPackagesFromUrl(url);
                    updates.AddRange(newPackages);
                }
                catch (Exception e)
                {
                    var webResponse = e is WebException webException ? webException.Response as HttpWebResponse : null;
                    if (webResponse != null && webResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Some web services, such as VSTS don't support the GetUpdates API. Attempt to retrieve updates via FindPackagesById.
                        NugetHelper.LogVerbose("{0} not found. Falling back to FindPackagesById.", url);
                        return GetUpdatesFallback(packagesCollection, includePrerelease, includeAllVersions, targetFrameworks, versionConstraints);
                    }

                    Debug.LogErrorFormat("Unable to retrieve package list from {0}\n{1}", url, e);
                }
            }

            // sort alphabetically, then by version descending
            updates.Sort();

#if TEST_GET_UPDATES_FALLBACK
            // Enable this define in order to test that GetUpdatesFallback is working as intended. This tests that it returns the same set of packages
            // that are returned by the GetUpdates API. Since GetUpdates isn't available when using a Visual Studio Team Services feed, the intention
            // is that this test would be conducted by using nuget.org's feed where both paths can be compared.
            List<NugetPackage> updatesReplacement =
 GetUpdatesFallback(installedPackages, includePrerelease, includeAllVersions, targetFrameworks, versionConstraints);
            ComparePackageLists(updates, updatesReplacement, "GetUpdatesFallback doesn't match GetUpdates API");
#endif

            return updates;
        }

        /// <inheritdoc />
        public void DownloadNupkgToFile(INugetPackageIdentifier package, string outputFilePath, string downloadUrlHint)
        {
            using (var objStream = NugetHelper.RequestUrl(downloadUrlHint, UserName, ExpandedPassword, null))
            {
                using (var fileStream = File.Create(outputFilePath))
                {
                    objStream.CopyTo(fileStream);
                }
            }
        }

#if TEST_GET_UPDATES_FALLBACK
        private static void ComparePackageLists(List<NugetPackage> updates,
            List<NugetPackage> updatesReplacement,
            string errorMessageToDisplayIfListsDoNotMatch)
        {
            var matchingComparison = new StringBuilder();
            var missingComparison = new StringBuilder();
            foreach (var package in updates)
            {
                if (updatesReplacement.Contains(package))
                {
                    matchingComparison.Append(matchingComparison.Length == 0 ? "Matching: " : ", ");
                    matchingComparison.Append(package);
                }
                else
                {
                    missingComparison.Append(missingComparison.Length == 0 ? "Missing: " : ", ");
                    missingComparison.Append(package);
                }
            }

            var extraComparison = new StringBuilder();
            foreach (var package in updatesReplacement)
            {
                if (!updates.Contains(package))
                {
                    extraComparison.Append(extraComparison.Length == 0 ? "Extra: " : ", ");
                    extraComparison.Append(package);
                }
            }

            if (missingComparison.Length > 0 || extraComparison.Length > 0)
            {
                Debug.LogWarningFormat(
                    "{0}\n{1}\n{2}\n{3}",
                    errorMessageToDisplayIfListsDoNotMatch,
                    matchingComparison,
                    missingComparison,
                    extraComparison);
            }
        }
#endif

        /// <inheritdoc />
        public void OnBeforeSerialize()
        {
            // do nothing
        }

        /// <inheritdoc />
        public void OnAfterDeserialize()
        {
            if (string.IsNullOrEmpty(SavedPassword))
            {
                SavedPassword = null;
            }
        }

        /// <summary>
        ///     Builds a list of NugetPackages from the XML returned from the HTTP GET request issued at the given URL.
        ///     Note that NuGet uses an Atom-feed (XML Syndicaton) superset called OData.
        ///     See here http://www.odata.org/documentation/odata-version-2-0/uri-conventions/.
        /// </summary>
        private List<INugetPackage> GetPackagesFromUrl(string url)
        {
            NugetHelper.LogVerbose("Getting packages from: {0}", url);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var packages = new List<INugetPackage>();
            using (var responseStream = NugetHelper.RequestUrl(url, UserName, ExpandedPassword, 10000))
            {
                using (var streamReader = new StreamReader(responseStream))
                {
                    var responsePackages = NugetODataResponse.Parse(XDocument.Load(streamReader), this);
                    foreach (var package in responsePackages)
                    {
                        packages.Add(package);
                    }
                }
            }

            NugetHelper.LogVerbose("Received {0} packages in {1} ms", packages.Count, stopwatch.ElapsedMilliseconds);

            return packages;
        }

        /// <summary>
        ///     Some NuGet feeds such as Visual Studio Team Services do not implement the GetUpdates function.
        ///     In that case this fallback function can be used to retrieve updates by using the FindPackagesById function.
        /// </summary>
        /// <param name="installedPackages">The list of currently installed packages.</param>
        /// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
        /// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
        /// <param name="targetFrameworks">The specific frameworks to target?.</param>
        /// <param name="versionConstraints">The version constraints?.</param>
        /// <returns>A list of all updates available.</returns>
        private List<INugetPackage> GetUpdatesFallback(
            IEnumerable<INugetPackage> installedPackages,
            bool includePrerelease = false,
            bool includeAllVersions = false,
            string targetFrameworks = "",
            string versionConstraints = "")
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            Debug.Assert(
                string.IsNullOrEmpty(targetFrameworks) &&
                string.IsNullOrEmpty(versionConstraints)); // These features are not supported by this version of GetUpdates.

            var updates = new List<INugetPackage>();
            foreach (var installedPackage in installedPackages)
            {
                var versionRange = $"({installedPackage.Version},)"; // Minimum of Current ID (exclusive) with no maximum (exclusive).
                var id = new NugetPackageIdentifier(installedPackage.Id, versionRange);
                var packageUpdates = FindPackagesById(id);

                if (!includePrerelease)
                {
                    packageUpdates.RemoveAll(p => p.IsPrerelease);
                }

                if (packageUpdates.Count == 0)
                {
                    continue;
                }

                var skip = includeAllVersions ? 0 : packageUpdates.Count - 1;
                updates.AddRange(packageUpdates.Skip(skip));
            }

            NugetHelper.LogVerbose("NugetPackageSource.GetUpdatesFallback took {0} ms", stopwatch.ElapsedMilliseconds);
            return updates;
        }
    }
}
