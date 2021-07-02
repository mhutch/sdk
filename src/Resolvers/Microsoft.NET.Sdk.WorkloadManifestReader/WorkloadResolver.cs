﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.NET.Sdk.Localization;
using FXVersion = Microsoft.DotNet.MSBuildSdkResolver.FXVersion;

namespace Microsoft.NET.Sdk.WorkloadManifestReader
{
    /// <remarks>
    /// This very specifically exposes only the functionality needed right now by the MSBuild workload resolver
    /// and by the template engine. More general APIs will be added later.
    /// </remarks>
    public class WorkloadResolver : IWorkloadResolver
    {
        private readonly Dictionary<string, WorkloadManifest> _manifests = new Dictionary<string, WorkloadManifest>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<WorkloadId, (WorkloadDefinition workload, WorkloadManifest manifest)> _workloads = new Dictionary<WorkloadId, (WorkloadDefinition, WorkloadManifest)>();
        private readonly Dictionary<WorkloadPackId, (WorkloadPack pack, WorkloadManifest manifest)> _packs = new Dictionary<WorkloadPackId, (WorkloadPack, WorkloadManifest)>();
        private IWorkloadManifestProvider? _manifestProvider;
        private string[] _currentRuntimeIdentifiers;
        private readonly string [] _dotnetRootPaths;

        private Func<string, bool>? _fileExistOverride;
        private Func<string, bool>? _directoryExistOverride;

        public static WorkloadResolver Create(IWorkloadManifestProvider manifestProvider, string dotnetRootPath, string sdkVersion)
        {
            string runtimeIdentifierChainPath = Path.Combine(dotnetRootPath, "sdk", sdkVersion, "NETCoreSdkRuntimeIdentifierChain.txt");
            string[] currentRuntimeIdentifiers = File.Exists(runtimeIdentifierChainPath) ?
                File.ReadAllLines(runtimeIdentifierChainPath).Where(l => !string.IsNullOrEmpty(l)).ToArray() :
                new string[] { };

            var packRootEnvironmentVariable = Environment.GetEnvironmentVariable("DOTNETSDK_WORKLOAD_PACK_ROOTS");

            string[] dotnetRootPaths;
            if (!string.IsNullOrEmpty(packRootEnvironmentVariable))
            {
                dotnetRootPaths = packRootEnvironmentVariable.Split(Path.DirectorySeparatorChar).Append(dotnetRootPath).ToArray();
            }
            else
            {
                dotnetRootPaths = new[] { dotnetRootPath };
            }

            return new WorkloadResolver(manifestProvider, dotnetRootPaths, currentRuntimeIdentifiers);
        }

        public static WorkloadResolver CreateForTests(IWorkloadManifestProvider manifestProvider, string[] dotNetRootPaths, string[]? currentRuntimeIdentifiers = null)
        {
            if (currentRuntimeIdentifiers == null)
            {
                currentRuntimeIdentifiers = new[] { "win-x64", "win", "any", "base" };
            }
            return new WorkloadResolver(manifestProvider, dotNetRootPaths, currentRuntimeIdentifiers);
        }

        /// <summary>
        /// Creates a resolver by composing all the manifests from the provider.
        /// </summary>
        private WorkloadResolver(IWorkloadManifestProvider manifestProvider, string [] dotnetRootPaths, string [] currentRuntimeIdentifiers)
            : this (dotnetRootPaths, currentRuntimeIdentifiers)
        {
            _manifestProvider = manifestProvider;

            LoadManifestsFromProvider(manifestProvider);
            ComposeWorkloadManifests();
        }

        /// <summary>
        /// Creates a resolver with no manifests.
        /// </summary>A
        private WorkloadResolver(string[] dotnetRootPaths, string[] currentRuntimeIdentifiers)
        {
            _dotnetRootPaths = dotnetRootPaths;
            _currentRuntimeIdentifiers = currentRuntimeIdentifiers;
        }

        public void RefreshWorkloadManifests()
        {
            if (_manifestProvider == null)
            {
                throw new InvalidOperationException("Resolver was created without provider and cannot be refreshed");
            }
            _manifests.Clear();
            LoadManifestsFromProvider(_manifestProvider);
            ComposeWorkloadManifests();
        }

        private void LoadManifestsFromProvider(IWorkloadManifestProvider manifestProvider)
        {
            foreach ((string manifestId, string? informationalPath, Func<Stream> openManifestStream) in manifestProvider.GetManifests())
            {
                using (var manifestStream = openManifestStream())
                {
                    var manifest = WorkloadManifestReader.ReadWorkloadManifest(manifestId, manifestStream, informationalPath);
                    if (!_manifests.TryAdd(manifestId, manifest))
                    {
                        throw new WorkloadManifestCompositionException($"Duplicate manifest '{manifestId}' from provider {manifestProvider}");
                    }
                }
            }
        }

        private void ComposeWorkloadManifests()
        {
            _workloads.Clear();
            _packs.Clear();

            foreach (var manifest in _manifests.Values)
            {
                if (manifest.DependsOnManifests != null)
                {
                    foreach (var dependency in manifest.DependsOnManifests)
                    {
                        if (_manifests.TryGetValue(dependency.Key, out var resolvedDependency))
                        {
                            if (FXVersion.Compare(dependency.Value, resolvedDependency.ParsedVersion) > 0)
                            {
                                throw new WorkloadManifestCompositionException($"Inconsistency in workload manifest '{manifest.Id}' ({manifest.InformationalPath}): requires '{dependency.Key}' version at least {dependency.Value} but found {resolvedDependency.Version}");
                            }
                        }
                        else
                        {
                            throw new WorkloadManifestCompositionException($"Inconsistency in workload manifest '{manifest.Id}' ({manifest.InformationalPath}): missing dependency '{dependency.Key}'");
                        }
                    }
                }

                HashSet<WorkloadRedirect>? redirects = null;
                foreach (var workload in manifest.Workloads)
                {
                    if (workload.Value is WorkloadRedirect redirect)
                    {
                        (redirects ??= new HashSet<WorkloadRedirect>()).Add(redirect);
                    }
                    else
                    {
                        if (!_workloads.TryAdd(workload.Key, ((WorkloadDefinition)workload.Value, manifest)))
                        {
                            WorkloadManifest conflictingManifest = _workloads[workload.Key].manifest;
                            throw new WorkloadManifestCompositionException($"Workload '{workload.Key}' in manifest '{manifest.Id}' ({manifest.InformationalPath}) conflicts with manifest '{conflictingManifest.Id}' ({conflictingManifest.InformationalPath})");
                        }
                    }
                }

                // resolve redirects upfront so they are transparent to the rest of the code
                // the _workloads dictionary maps redirected ids directly to the replacement
                if (redirects != null)
                {
                    // handle multi-levels redirects via multiple resolve passes, bottom-up
                    while (redirects.RemoveWhere(redirect =>
                    {
                        if (_workloads.TryGetValue(redirect.ReplaceWith, out var replacement))
                        {
                            if (!_workloads.TryAdd(redirect.Id, replacement))
                            {
                                WorkloadManifest conflictingManifest = _workloads[redirect.Id].manifest;
                                throw new WorkloadManifestCompositionException($"Workload '{redirect.Id}' in manifest '{manifest.Id}' ({manifest.InformationalPath}) conflicts with manifest '{conflictingManifest.Id}' ({conflictingManifest.InformationalPath})");
                            }
                            return true;
                        }
                        return false;
                    }) > 0) { };

                    if (redirects.Count > 0)
                    {
                        throw new WorkloadManifestCompositionException(Strings.UnresolvedWorkloadRedirects, string.Join("\", \"", redirects.Select(r => r.Id.ToString())));
                    }
                }

                foreach (var pack in manifest.Packs)
                {
                    if (!_packs.TryAdd(pack.Key, (pack.Value, manifest)))
                    {
                        WorkloadManifest conflictingManifest = _packs[pack.Key].manifest;
                        throw new WorkloadManifestCompositionException($"Workload pack '{pack.Key}' in manifest '{manifest.Id}' ({manifest.InformationalPath}) conflicts with manifest '{conflictingManifest.Id}' ({conflictingManifest.InformationalPath})");
                    }
                }
            }
        }

        /// <summary>
        /// Gets the installed workload packs of a particular kind
        /// </summary>
        /// <remarks>
        /// Used by MSBuild resolver to scan SDK packs for AutoImport.props files to be imported.
        /// Used by template engine to find templates to be added to hive.
        /// </remarks>
        public IEnumerable<PackInfo> GetInstalledWorkloadPacksOfKind(WorkloadPackKind kind)
        {
            foreach ((var pack, _) in _packs.Values)
            {
                if (pack.Kind != kind)
                {
                    continue;
                }

                if (ResolveId(pack) is WorkloadPackId resolvedPackageId)
                {
                    var aliasedPath = GetPackPath(_dotnetRootPaths, resolvedPackageId, pack.Version, pack.Kind, out bool isInstalled);
                    if (isInstalled)
                    {
                        yield return CreatePackInfo(pack, aliasedPath, resolvedPackageId);
                    }
                }
            }
        }

        internal void ReplaceFilesystemChecksForTest(Func<string, bool> fileExists, Func<string, bool> directoryExists)
        {
            _fileExistOverride = fileExists;
            _directoryExistOverride = directoryExists;
        }

        private PackInfo CreatePackInfo(WorkloadPack pack, string aliasedPath, WorkloadPackId resolvedPackageId) => new PackInfo(
                pack.Id,
                pack.Version,
                pack.Kind,
                aliasedPath,
                resolvedPackageId.ToString()
            );

        /// <summary>
        /// Resolve the package ID for the host platform.
        /// </summary>
        /// <param name="pack">The workload pack</param>
        /// <returns>The path to the pack, or null if the pack is not available on the host platform.</returns>
        private WorkloadPackId? ResolveId(WorkloadPack pack)
        {
            if (!pack.IsAlias)
            {
                return pack.Id;
            }

            if (pack.TryGetAliasForRuntimeIdentifiers(_currentRuntimeIdentifiers) is WorkloadPackId aliasedId)
            {
                return aliasedId;
            }

            return null;
        }

        /// <summary>
        /// Resolve the pack path for the host platform.
        /// </summary>
        /// <param name="pack">The workload pack</param>
        /// <param name="isInstalled">Whether the pack is installed</param>
        /// <returns>The path to the pack, or null if the pack is not available on the host platform.</returns>
        private string? ResolvePackPath(WorkloadPack pack, out bool isInstalled)
        {
            if (ResolveId(pack) is WorkloadPackId resolvedId)
            {
                return GetPackPath(_dotnetRootPaths, resolvedId, pack.Version, pack.Kind, out isInstalled);
            }

            isInstalled = false;
            return null;
        }

        private string GetPackPath(string [] dotnetRootPaths, WorkloadPackId packageId, string packageVersion, WorkloadPackKind kind, out bool isInstalled)
        {
            isInstalled = false;
            string packPath = "";
            bool isFile;
            foreach (var rootPath in dotnetRootPaths)
            {
                switch (kind)
                {
                    case WorkloadPackKind.Framework:
                    case WorkloadPackKind.Sdk:
                        packPath = Path.Combine(rootPath, "packs", packageId.ToString(), packageVersion);
                        isFile = false;
                        break;
                    case WorkloadPackKind.Template:
                        packPath = Path.Combine(rootPath, "template-packs", packageId.GetNuGetCanonicalId() + "." + packageVersion.ToLowerInvariant() + ".nupkg");
                        isFile = true;
                        break;
                    case WorkloadPackKind.Library:
                        packPath = Path.Combine(rootPath, "library-packs", packageId.GetNuGetCanonicalId() + "." + packageVersion.ToLowerInvariant() + ".nupkg");
                        isFile = true;
                        break;
                    case WorkloadPackKind.Tool:
                        packPath = Path.Combine(rootPath, "tool-packs", packageId.ToString(), packageVersion);
                        isFile = false;
                        break;
                    default:
                        throw new ArgumentException($"The package kind '{kind}' is not known", nameof(kind));
                }

                //can we do a more robust check than directory.exists?
                isInstalled = isFile ?
                    _fileExistOverride?.Invoke(packPath) ?? File.Exists(packPath) :
                    _directoryExistOverride?.Invoke(packPath) ?? Directory.Exists(packPath); ;

                if (isInstalled)
                {
                    break;
                }
            }
            return packPath;
        }

        /// <summary>
        /// Gets the IDs of all the packs that are installed
        /// </summary>
        private HashSet<WorkloadPackId> GetInstalledPacks()
        {
            var installedPacks = new HashSet<WorkloadPackId>();
            foreach ((WorkloadPackId id, (WorkloadPack pack, WorkloadManifest _)) in _packs)
            {
                ResolvePackPath(pack, out bool isInstalled);
                if (isInstalled)
                {
                    installedPacks.Add(id);
                }
            }
            return installedPacks;
        }

        public IEnumerable<WorkloadPackId> GetPacksInWorkload(WorkloadId workloadId)
        {
            if (string.IsNullOrEmpty(workloadId))
            {
                throw new ArgumentException($"'{nameof(workloadId)}' cannot be null or empty", nameof(workloadId));
            }

            if (!_workloads.TryGetValue(workloadId, out var value))
            {
                throw new Exception($"Workload not found: {workloadId}. Known workloads: {string.Join(" ", _workloads.Select(workload => workload.Key.ToString()))}");
            }
            var workload = value.workload;

            if (workload.Extends?.Count > 0)
            {
                return GetPacksInWorkload(workload);
            }

#nullable disable
            return workload.Packs ?? Enumerable.Empty<WorkloadPackId>();
#nullable restore
        }

        internal IEnumerable<WorkloadPackId> GetPacksInWorkload(WorkloadDefinition workload)
        {
            var dedup = new HashSet<WorkloadId>();

            IEnumerable<WorkloadPackId> ExpandPacks (WorkloadId workloadId)
            {
                if (!(_workloads.TryGetValue (workloadId) is (WorkloadDefinition workloadInfo, _)))
                {
                    // inconsistent manifest
                    throw new Exception("Workload not found");
                }

                if (workloadInfo.Packs != null && workloadInfo.Packs.Count > 0)
                {
                    foreach (var p in workloadInfo.Packs)
                    {
                        yield return p;
                    }
                }

                if (workloadInfo.Extends != null && workloadInfo.Extends.Count > 0)
                {
                    foreach (var e in workloadInfo.Extends)
                    {
                        if (dedup.Add(e))
                        {
                            foreach (var ep in ExpandPacks(e))
                            {
                                yield return ep;
                            }
                        }
                    }
                }
            }

            return ExpandPacks(workload.Id);
        }

        /// <summary>
        /// Gets the version of a workload pack for this resolver's SDK band
        /// </summary>
        /// <remarks>
        /// Used by the MSBuild SDK resolver to look up which versions of the SDK packs to import.
        /// </remarks>
        public PackInfo? TryGetPackInfo(WorkloadPackId packId)
        {
            if (string.IsNullOrEmpty(packId))
            {
                throw new ArgumentException($"'{nameof(packId)}' cannot be null or empty", nameof(packId));
            }

            if (_packs.TryGetValue(new WorkloadPackId (packId)) is (WorkloadPack pack, _))
            {
                if (ResolveId(pack) is WorkloadPackId resolvedPackageId)
                {
                    var aliasedPath = GetPackPath(_dotnetRootPaths, resolvedPackageId, pack.Version, pack.Kind, out _);
                    return CreatePackInfo(pack, aliasedPath, resolvedPackageId);
                }
            }

            return null;
        }

        /// <summary>
        /// Recommends a set of workloads should be installed on top of the existing installed workloads to provide the specified missing packs
        /// </summary>
        /// <remarks>
        /// Used by the MSBuild workload resolver to emit actionable errors
        /// </remarks>
        public ISet<WorkloadInfo> GetWorkloadSuggestionForMissingPacks(IList<WorkloadPackId> packIds)
        {
            var requestedPacks = new HashSet<WorkloadPackId>(packIds);
            var expandedWorkloads = _workloads.Select(w => (w.Key, new HashSet<WorkloadPackId>(GetPacksInWorkload(w.Value.workload))));
            var finder = new WorkloadSuggestionFinder(GetInstalledPacks(), requestedPacks, expandedWorkloads);

            return new HashSet<WorkloadInfo>
            (
                finder.GetBestSuggestion().Workloads.Select(s => new WorkloadInfo(s, _workloads[s].workload.Description))
            );
        }

        /// <summary>
        /// Returns the list of workloads defined by the manifests on disk
        /// </summary>
        public IEnumerable<WorkloadDefinition> GetAvailableWorkloads()
        {
            foreach ((WorkloadId _, (WorkloadDefinition workload, WorkloadManifest _)) in _workloads)
            {
                yield return workload;
            }
        }

        /// <summary>
        /// Determines which of the installed workloads has updates available in the advertising manifests.
        /// </summary>
        /// <param name="advertisingManifestResolver">A resolver that composes the advertising manifests with the installed manifests that do not have corresponding advertising manifests</param>
        /// <param name="existingWorkloads">The IDs of all of the installed workloads</param>
        /// <returns></returns>
        public IEnumerable<WorkloadId> GetUpdatedWorkloads(WorkloadResolver advertisingManifestResolver, IEnumerable<WorkloadId> installedWorkloads)
        {
            foreach(var workloadId in installedWorkloads)
            {
                var existingWorkload = _workloads[workloadId].workload;
                var existingPacks = GetPacksInWorkload(existingWorkload).ToHashSet();
                var updatedWorkload = advertisingManifestResolver._workloads[workloadId].workload;
                var updatedPacks = advertisingManifestResolver.GetPacksInWorkload(updatedWorkload);

                if (!existingPacks.SetEquals(updatedPacks) || existingPacks.Any(p=> PackHasChanged(_packs[p].pack, advertisingManifestResolver._packs[p].pack)))
                {
                    yield return workloadId;
                }
            }
        }

        private bool PackHasChanged(WorkloadPack oldPack, WorkloadPack newPack)
        {
            var existingPackResolvedId = ResolveId(oldPack);
            var newPackResolvedId = ResolveId(newPack);
            if (existingPackResolvedId is null && newPackResolvedId is null)
            {
                return false; // pack still aliases to nothing
            }
            else if (existingPackResolvedId is null || newPackResolvedId is null || !existingPackResolvedId.Value.Equals(newPackResolvedId.Value))
            {
                return true; // alias has changed
            }
            if (!string.Equals(oldPack.Version, newPack.Version, StringComparison.OrdinalIgnoreCase))
            {
                return true; // version has changed
            }
            return false;
        }

        public IWorkloadResolver CreateOverlayResolver(IWorkloadManifestProvider overlayManifestProvider)
        {
            // we specifically don't assign the overlayManifestProvider to the new resolver
            // because it's not possible to refresh an overlay resolver
            var overlayResolver = new WorkloadResolver(_dotnetRootPaths, _currentRuntimeIdentifiers);
            overlayResolver.LoadManifestsFromProvider(overlayManifestProvider);

            // after loading the overlay manifests into the new resolver
            // we add all the manifests from this resolver that are not overlayed
            foreach (var manifest in _manifests)
            {
                overlayResolver._manifests.TryAdd(manifest.Key, manifest.Value);
            }

            overlayResolver.ComposeWorkloadManifests();

            return overlayResolver;
        }

        public class PackInfo
        {
            public PackInfo(WorkloadPackId id, string version, WorkloadPackKind kind, string path, string resolvedPackageId)
            {
                Id = id;
                Version = version;
                Kind = kind;
                Path = path;
                ResolvedPackageId = resolvedPackageId;
            }

            /// <summary>
            /// The workload pack ID. The NuGet package ID <see cref="ResolvedPackageId"/> may differ from this.
            /// </summary>
            public WorkloadPackId Id { get; }

            public string Version { get; }

            public WorkloadPackKind Kind { get; }

            public string ResolvedPackageId { get; }

            /// <summary>
            /// Path to the pack. If it's a template or library pack, <see cref="IsStillPacked"/> will be <code>true</code> and this will be a path to the <code>nupkg</code>,
            /// else <see cref="IsStillPacked"/> will be <code>false</code> and this will be a path to the directory into which it has been unpacked.
            /// </summary>
            public string Path { get; }

            /// <summary>
            /// Whether the pack pointed to by the path is still in a packed form.
            /// </summary>
            public bool IsStillPacked => Kind switch
            {
                WorkloadPackKind.Library => false,
                WorkloadPackKind.Template => false,
                _ => true
            };
        }

        public class WorkloadInfo
        {
            public WorkloadInfo(WorkloadId id, string? description)
            {
                Id = id;
                Description = description;
            }

            public WorkloadId Id { get; }
            public string? Description { get; }
        }

        public WorkloadInfo GetWorkloadInfo(WorkloadId WorkloadId)
        {
            if (_workloads.TryGetValue(WorkloadId) is (WorkloadDefinition workload, _))
            {
                return new WorkloadInfo(workload.Id, workload.Description);
            }
            throw new Exception("Workload not found");
        }

        public bool IsWorkloadPlatformCompatible(WorkloadId workloadId)
        {
            var workloadDef = GetAvailableWorkloads().FirstOrDefault(workload => workload.Id.ToString().Equals(workloadId.ToString()));
            if (workloadDef == null)
            {
                throw new Exception("Workload not found");
            }
            if (workloadDef.Platforms == null)
            {
                return true;
            }
            return workloadDef.Platforms.Any(supportedPlatform => _currentRuntimeIdentifiers.Contains(supportedPlatform));
        }
    }

    static class DictionaryExtensions
    {
#if !NETCOREAPP
        public static bool TryAdd<TKey,TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value) where TKey : notnull
        {
            if (dictionary.ContainsKey(key))
            {
                return false;
            }
            dictionary.Add(key, value);
            return true;
        }

        public static void Deconstruct<TKey,TValue>(this KeyValuePair<TKey,TValue> kvp, out TKey key, out TValue value)
        {
            key = kvp.Key;
            value = kvp.Value;
        }
#endif

        public static TValue? TryGetValue<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key)
            where TKey : notnull
            where TValue : struct
        {
            if (dictionary.TryGetValue(key, out TValue value))
            {
                return value;
            }
            return default(TValue?);
        }
    }
}
