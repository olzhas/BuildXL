﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Distributed.NuCache.CopyScheduling;
using BuildXL.Cache.ContentStore.Distributed.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Distributed;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using ContentStore.Grpc;

namespace BuildXL.Cache.ContentStore.Distributed.Stores
{
    /// <summary>
    /// Configuration object for <see cref="DistributedContentCopier"/> and <see cref="DistributedContentStore"/> classes.
    /// </summary>
    public sealed class DistributedContentStoreSettings
    {
        /// <summary>
        /// Default value for <see cref="ParallelCopyFilesLimit"/>
        /// </summary>
        public const int DefaultParallelCopyFilesLimit = 8;

        /// <summary>
        /// Delays for retries for file copies
        /// </summary>
        private static readonly List<TimeSpan> CacheCopierDefaultRetryIntervals = new List<TimeSpan>()
        {
            // retry the first 2 times quickly.
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),

            // then back-off exponentially.
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),

            // Borrowed from Empirical CacheV2 determined to be appropriate for general remote server restarts.
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
        };

        /// <summary>
        /// Files smaller than this should use the untrusted hash.
        /// </summary>
        public long TrustedHashFileSizeBoundary { get; set; } = -1;

        /// <summary>
        /// Whether the underlying content store should be told to trust a hash when putting content.
        /// </summary>
        /// <remarks>
        /// When trusted, then distributed file copier will hash the file and the store won't re-hash the file.
        /// </remarks>
        public bool UseTrustedHash(long fileSize)
        {
            // Only use trusted hash for files greater than _trustedHashFileSizeBoundary. Over a few weeks of data collection, smaller files appear to copy and put faster using the untrusted variant.
            return fileSize >= TrustedHashFileSizeBoundary;
        }

        /// <summary>
        /// Files longer than this will be hashed concurrently with the download.
        /// All bytes downloaded before this boundary is met will be hashed inline.
        /// </summary>
        public long ParallelHashingFileSizeBoundary { get; set; }

        /// <summary>
        /// <see cref="CopySchedulerConfiguration"/>
        /// </summary>
        public CopySchedulerConfiguration CopyScheduler { get; set; } = new CopySchedulerConfiguration();

        /// <summary>
        /// Maximum number of files to copy locally in parallel for a given operation
        /// </summary>
        public int ParallelCopyFilesLimit { get; set; } = DefaultParallelCopyFilesLimit;

        /// <summary>
        /// Delays for retries for file copies
        /// </summary>
        public IReadOnlyList<TimeSpan> RetryIntervalForCopies { get; set; } = CacheCopierDefaultRetryIntervals;

        /// <summary>
        /// Per-copy bandwidth options that allow more aggressive copy cancellation for earlier attempts.
        /// </summary>
        public IReadOnlyList<BandwidthConfiguration> BandwidthConfigurations { get; set; }

        /// <summary>
        /// Controls the maximum total number of copy retry attempts
        /// </summary>
        public int MaxRetryCount { get; set; } = 32;

        /// <summary>
        /// Indicates whether proactive copies will trace successful results
        /// </summary>
        public bool TraceProactiveCopy { get; set; } = false;

        /// <summary>
        /// The mode in which proactive copy should run
        /// </summary>
        public ProactiveCopyMode ProactiveCopyMode { get; set; } = ProactiveCopyMode.Disabled;

        /// <summary>
        /// Whether to perform a proactive copy after putting a file.
        /// </summary>
        public bool ProactiveCopyOnPut { get; set; } = true;

        /// <summary>
        /// Whether to perform a proactive copy after copying because of a pin.
        /// </summary>
        public bool ProactiveCopyOnPin { get; set; } = false;

        /// <summary>
        /// Whether to push the content. If disabled, the copy will be requested and the target machine then will pull.
        /// </summary>
        public bool PushProactiveCopies { get; set; } = false;

        /// <summary>
        /// Whether to use the preferred locations for proactive copies.
        /// </summary>
        public bool ProactiveCopyUsePreferredLocations { get; set; } = false;

        /// <summary>
        /// Should only be used for testing to inline the operations like proactive copy.
        /// </summary>
        public bool InlineOperationsForTests { get; set; } = false;

        /// <summary>
        /// Maximum number of locations which should trigger a proactive copy.
        /// </summary>
        public int ProactiveCopyLocationsThreshold { get; set; } = 3;

        /// <summary>
        /// Whether to reject push copies based on whether we've evicted something younger recently.
        /// </summary>
        public bool ProactiveCopyRejectOldContent { get; set; } = false;

        /// <summary>
        /// Number of copy attempts which should be restricted in its number or replicas.
        /// </summary>
        public int CopyAttemptsWithRestrictedReplicas { get; set; } = 0;

        /// <summary>
        /// Number of replicas to attempt when a copy is being restricted.
        /// </summary>
        public int RestrictedCopyReplicaCount { get; set; } = 3;

        /// <summary>
        /// Whether to enable proactive replication
        /// </summary>
        public bool EnableProactiveReplication { get; set; } = false;

        /// <summary>
        /// The interval between proactive replication interations
        /// </summary>
        public TimeSpan ProactiveReplicationInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Minimum delay between individual content proactive replications.
        /// </summary>
        public TimeSpan DelayForProactiveReplication { get; set; } = TimeSpan.FromMinutes(0.5);

        /// <summary>
        /// The maximum amount of copies allowed per proactive replication invocation.
        /// </summary>
        public int ProactiveReplicationCopyLimit { get; set; } = 5;

        /// <summary>
        /// The amount of time for nagling GetBulk (locations) for proactive copy operations
        /// </summary>
        public TimeSpan ProactiveCopyGetBulkInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Expiry for in-ring machines cache.
        /// </summary>
        public TimeSpan ProactiveCopyInRingMachineLocationsExpiryCache { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// The size of nagle batch for proactive copy get bulk
        /// </summary>
        public int ProactiveCopyGetBulkBatchSize { get; set; } = 20;

        /// <summary>
        /// Amount of times that a proactive copy is allowed to retry
        /// </summary>
        public int ProactiveCopyMaxRetries { get; set; } = 0;

        /// <summary>
        /// Defines pinning behavior
        /// </summary>
        public PinConfiguration PinConfiguration { get; set; }

        /// <nodoc />
        public static DistributedContentStoreSettings DefaultSettings { get; } = new DistributedContentStoreSettings();

        /// <summary>
        /// Maximum number of PutFile and PlaceFile operations that can happen concurrently.
        /// </summary>
        public int MaximumConcurrentPutAndPlaceFileOperations { get; set; } = 512;

        /// <summary>
        /// Indicates whether a post initialization task is set to complete after startup to force local eviction to wait
        /// for distributed store initialization to complete.
        /// </summary>
        public bool SetPostInitializationCompletionAfterStartup { get; set; } = true;

        /// <summary>
        /// Every time interval we trace a report on copy progression.
        /// </summary>
        public TimeSpan PeriodicCopyTracingInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Minimum size to start compressing gRPC transfers
        /// </summary>
        public long? GrpcCopyCompressionSizeThreshold { get; set; }

        /// <summary>
        /// Algorithm to use when a gRPC transfer is to be compressed
        /// </summary>
        public CopyCompression GrpcCopyCompressionAlgorithm { get; set; } = CopyCompression.Gzip;

        /// <summary>
        /// If true, then the in-ring machines are used as the candidates for file copies.
        /// </summary>
        public bool UseInRingMachinesForCopies { get; set; }

        /// <summary>
        /// If true, the build id will be put into the cache as the file to prevent the removal of this information during reconciliation.
        /// </summary>
        public bool StoreBuildIdInCache { get; set; }

        /// <summary>
        /// If true, put calls with <see cref="UrgencyHint.SkipRegisterContent"/> do not register content.
        /// </summary>
        public bool RespectSkipRegisterContentHint { get; set; }

        /// <summary>
        /// If true, then the content that was added to the cache will be eagerly registered in a global store.
        /// </summary>
        public bool RegisterEagerlyOnPut { get; set; }
    }
}
