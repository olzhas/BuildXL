// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Tracing;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// Performance counters available for <see cref="ContentLocationDatabase"/>.
    /// </summary>
    public enum ContentLocationDatabaseCounters
    {
        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        PutLocations,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        GetLocations,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        GarbageCollect,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        LocationAdded,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        LocationRemoved,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        ContentTouched,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        SaveCheckpoint,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        RestoreCheckpoint,

        /// <nodoc />
        TotalNumberOfCreatedEntries,

        /// <nodoc />
        TotalNumberOfSkippedEntryTouches,

        /// <nodoc />
        TotalNumberOfDeletedEntries,

        /// <summary>
        /// Total number of entries collected during the garbage collection process.
        /// </summary>
        TotalNumberOfCollectedEntries,

        /// <summary>
        /// Total number of entries cleaned during the garbage collection process (i.e. entries that were updated based on inactive machines).
        /// </summary>
        TotalNumberOfCleanedEntries,

        /// <summary>
        /// Total number of entries cleaned during the garbage collection process.
        /// </summary>
        TotalNumberOfScannedEntries,

        /// <nodoc />
        TotalNumberOfCacheHit,

        /// <nodoc />
        TotalNumberOfCacheMiss,

        /// <summary>
        /// Number of times database has been cleaned and loaded empty
        /// </summary>
        DatabaseCleans,

        /// <summary>
        /// Number of times database epoch has not matched prior loaded instance
        /// </summary>
        EpochMismatches,

        /// <summary>
        /// Number of times database epoch has matched prior loaded instance
        /// </summary>
        EpochMatches,

        /// <nodoc />
        NumberOfGetOperations,

        /// <nodoc />
        NumberOfStoreOperations,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        GarbageCollectMetadata,

        /// <nodoc />
        GarbageCollectMetadataEntriesScanned,

        /// <nodoc />
        GarbageCollectMetadataEntriesRemoved,

        /// <nodoc />
        [CounterType(CounterType.Stopwatch)]
        GarbageCollectContent,

        /// <summary>
        /// Amount of unique content added in bytes
        /// </summary>
        UniqueContentAddedSize,

        /// <summary>
        /// Amount of content added in bytes
        /// </summary>
        TotalContentAddedSize,

        /// <summary>
        /// Count of content added
        /// </summary>
        TotalContentAddedCount,

        /// <summary>
        /// Amount of unique content removed in bytes
        /// </summary>
        UniqueContentRemovedSize,

        /// <summary>
        /// Amount of content removed in bytes
        /// </summary>
        TotalContentRemovedSize,

        /// <summary>
        /// Amount of content removed
        /// </summary>
        TotalContentRemovedCount,

        /// <summary>
        /// Tracks the RocksDb merge operator invocation count and duration.
        /// </summary>
        [CounterType(CounterType.Stopwatch)]
        MergeEntry,

        /// <summary>
        /// Tracks the RocksDb merge operator used for sorted content location entries.
        /// </summary>
        [CounterType(CounterType.Stopwatch)]
        MergeEntrySorted,

        /// <summary>
        /// Tracks the duration of changing the machine locations in the database
        /// </summary>
        [CounterType(CounterType.Stopwatch)]
        SetMachineExistenceAndUpdateDatabase,

        /// <summary>
        /// The actual number of database changes after calling <see cref="SetMachineExistenceAndUpdateDatabase"/>.
        /// </summary>
        DatabaseChanges,

        /// <nodoc />
        MergeAdd,

        /// <nodoc />
        MergeTouch,

        /// <nodoc />
        MergeRemove,
    }
}
