// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Vsts;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Vsts;
using BuildXL.Utilities.Authentication;
using BuildXL.Utilities.ParallelAlgorithms;
using CLAP;

namespace BuildXL.Cache.MemoizationStore.VstsApp
{
    internal sealed partial class Application
    {
        /// <summary>
        ///     Incorporate all strong fingerprints into a session of the BuildCache service.
        /// </summary>
        // ReSharper disable once UnusedMember.Global
        [Verb(Aliases = "incorporate", Description = "Incorporate a set of strong fingerprints against a given account")]
        internal void IncorporateStrongFingerprints(
            [Required, Description("Path to log containing the strong fingerpints")] string log,
            [Required, Description("BuildCache url to which to incorporate the fingerprints")] string buildCacheUrl,
            [Required, Description("BlobStore url associated to the buildCache url")] string blobStoreUrl,
            [Required, Description("Cache namespace to target within BuildCache")] string cacheNamespace,
            [Required, Description("Whether or not to use the newer Blob implementation of BuildCache")] bool useBlobContentHashLists,
            [DefaultValue(false), Description("Whether or not to use aad authentication")] bool useAad,
            [DefaultValue(null), Description("Maximum number of fingerprint batches sent in parallel within a set. Default: System.Environment.ProcessorCount")] int? maxDegreeOfParallelism,
            [DefaultValue(null), Description("Maximum number of fingerprints per batch within a set. Default: 500")] int? maxFingerprintsPerRequest,
            [DefaultValue(1), Description("How many times to incorporate the set of fingerprints")] int iterationCount,
            [DefaultValue(1), Description("How many sets of fingerprints to incorporate in parallel")] int iterationDegreeOfParallelism)
        {
            Initialize();

            Stopwatch stopwatch = Stopwatch.StartNew();
            int count = 0;
            AbsolutePath logPath = new AbsolutePath(log);

            if (!_fileSystem.FileExists(logPath))
            {
                throw new ArgumentException($"Log file {log} does not exist.", nameof(log));
            }

            BuildCacheServiceConfiguration config = new BuildCacheServiceConfiguration(buildCacheUrl, blobStoreUrl)
            {
                CacheNamespace = cacheNamespace,
                UseAad = useAad,
                UseBlobContentHashLists = useBlobContentHashLists,
                FingerprintIncorporationEnabled = true
            };

            if (maxDegreeOfParallelism.HasValue)
            {
                config.MaxDegreeOfParallelismForIncorporateRequests = maxDegreeOfParallelism.Value;
            }

            if (maxFingerprintsPerRequest.HasValue)
            {
                config.MaxFingerprintsPerIncorporateRequest = maxFingerprintsPerRequest.Value;
            }

            using (var logStream = _fileSystem.OpenReadOnly(logPath, FileShare.Read | FileShare.Delete))
            using (StreamReader reader = new StreamReader(logStream))
            {
                Context context = new Context(_logger);
                RunBuildCacheAsync(
                    context,
                    config,
                    async cache =>
                    {
                        const string sessionName = "CommandLineSessionName";
                        const ImplicitPin implicitPin = ImplicitPin.None;

                        List<StrongFingerprint> strongFingerprints = EnumerateUniqueStrongFingerprints(reader).ToList();
                        count = strongFingerprints.Count;
                        _logger.Always($"Incorporating {count} strong fingerprints {iterationCount} times");

                        var incorporateBlock =
                            ActionBlockSlim.Create<List<StrongFingerprint>>(
                                degreeOfParallelism: iterationDegreeOfParallelism,
                                async fingerprints =>
                                {
                                    var iterationStopwatch = Stopwatch.StartNew();
                                    var iterationContext = context.CreateNested(nameof(IncorporateStrongFingerprints));
                                    CreateSessionResult<ICacheSession> createSessionResult = cache.CreateSession(
                                        iterationContext, sessionName, implicitPin);
                                    if (!createSessionResult.Succeeded)
                                    {
                                        _tracer.Error(iterationContext, $"Failed to create BuildCache session. Result=[{createSessionResult}]");
                                        return;
                                    }

                                    using (ICacheSession session = createSessionResult.Session)
                                    {
                                        BoolResult r = await session.StartupAsync(iterationContext);
                                        if (!r.Succeeded)
                                        {
                                            _tracer.Error(iterationContext, $"Failed to start up BuildCache client session. Result=[{r}]");
                                            return;
                                        }

                                        try
                                        {
                                            await session.IncorporateStrongFingerprintsAsync(
                                                iterationContext, strongFingerprints.AsTasks(), CancellationToken.None).IgnoreFailure();
                                        }
                                        finally
                                        {
                                            iterationStopwatch.Stop();
                                            r = await session.ShutdownAsync(iterationContext);
                                            if (r.Succeeded)
                                            {
                                                _tracer.Always(context,
                                                    $"Incorporated {count} fingerprints in {iterationStopwatch.ElapsedMilliseconds / 1000} seconds.");
                                            }
                                            else
                                            {
                                                _tracer.Error(iterationContext, $"Failed to shut down BuildCache client Session. Result=[{r}]");
                                            }
                                        }
                                    }
                                });

                        for (int i = 0; i < iterationCount; i++)
                        {
                            await incorporateBlock.PostAsync(strongFingerprints);
                        }

                        incorporateBlock.Complete();

                        await incorporateBlock.Completion;
                    }).Wait();
            }

            stopwatch.Stop();
            _logger.Always($"Incorporated {count} unique strong fingerprints {iterationCount} times in {stopwatch.ElapsedMilliseconds / 1000} seconds.");
        }

        private async Task RunBuildCacheAsync(
            Context context, BuildCacheServiceConfiguration config, Func<ICache, Task> funcAsync)
        {
            VssCredentialsFactory credentialsFactory = new VssCredentialsFactory(pat: null, new CredentialProviderHelper(m => _tracer.Info(context, m)), m => _tracer.Info(context, m));
            ICache cache = BuildCacheCacheFactory.Create(_fileSystem, _logger, credentialsFactory, config, null);

            using (cache)
            {
                BoolResult r = await cache.StartupAsync(context);
                if (!r.Succeeded)
                {
                    _tracer.Error(context, $"Failed to start up BuildCache client. Result=[{r}]");
                    return;
                }

                try
                {
                    await funcAsync(cache);
                }
                finally
                {
                    r = await cache.ShutdownAsync(context);
                    if (!r.Succeeded)
                    {
                        _tracer.Error(context, $"Failed to shut down BuildCache client. Result=[{r}]");
                    }
                }
            }
        }
    }
}
