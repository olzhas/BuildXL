﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Tracing;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.WindowsAzure.Storage;
using FileInfo = System.IO.FileInfo;
using OperationContext = BuildXL.Cache.ContentStore.Tracing.Internal.OperationContext;

namespace BuildXL.Cache.ContentStore.Vsts
{
    /// <summary>
    ///     IReadOnlyContentSession for DedupContentStore.
    /// </summary>
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class DedupReadOnlyContentSession : ContentSessionBase, IReadOnlyBackingContentSession
    {
        private enum Counters
        {
            PinInlined,
            PinIgnored
        }

        /// <inheritdoc />
        public BackingContentStoreExpiryCache ExpiryCache { get; } = new BackingContentStoreExpiryCache();

        /// <inheritdoc />
        public DownloadUriCache UriCache { get; } = new DownloadUriCache();

        private CounterCollection<BackingContentStore.SessionCounters> _counters { get; }
        private CounterCollection<Counters> _dedupCounters { get; }

        /// <summary>
        /// Default number of outstanding connections to throttle Artifact Services.
        /// TODO: Unify cache config - current default taken from IOGate in DistributedReadOnlyContentSession.
        /// </summary>
        protected const int DefaultMaxConnections = 512;

        /// <summary>
        /// If operation waits longer than this value to get past ConnectionGate, write warning to log.
        /// </summary>
        private const int MinLogWaitTimeInSeconds = 1;

        /// <summary>
        /// Default number of tasks to process in parallel.
        /// </summary>
        private const int DefaultMaxParallelism = 16;

        /// <summary>
        ///     Size for stream buffers to temp files.
        /// </summary>
        protected const int StreamBufferSize = 16384;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(DedupContentSession));

        //protected override Tracer Tracer => _tracer;

        /// <summary>
        ///     Staging ground for parallel upload/downloads.
        /// </summary>
        protected readonly DisposableDirectory TempDirectory;

        /// <summary>
        ///     File system.
        /// </summary>
        protected readonly IAbsFileSystem FileSystem;

        // Error codes: https://msdn.microsoft.com/en-us/library/windows/desktop/ms681382(v=vs.85).aspx
        private const int ErrorFileExists = 80;

        /// <summary>
        ///     Backing DedupStore client
        /// </summary>
        protected readonly IDedupStoreClient DedupStoreClient;

        /// <summary>
        ///     Gate to limit the number of outstanding connections to AS.
        /// </summary>
        protected readonly SemaphoreSlim ConnectionGate;

        /// <summary>
        ///     Expiration time of content in VSTS
        ///     Note: Determined by configurable timeToKeepContent. This is usually defined to be on the order of days.
        /// </summary>
        protected readonly DateTime EndDateTime;

        private readonly TimeSpan _pinInlineThreshold;

        private readonly TimeSpan _ignorePinThreshold;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobReadOnlyContentSession"/> class.
        /// </summary>
        /// <param name="fileSystem">Filesystem used to read/write files.</param>
        /// <param name="name">Session name.</param>
        /// <param name="dedupStoreHttpClient">Backing DedupStore http client.</param>
        /// <param name="timeToKeepContent">Minimum time-to-live for accessed content.</param>
        /// <param name="pinInlineThreshold">Maximum time-to-live to inline pin calls.</param>
        /// <param name="ignorePinThreshold">Minimum time-to-live to ignore pin calls.</param>
        /// <param name="maxConnections">The maximum number of outbound connections to VSTS.</param>
        /// <param name="counterTracker">Parent counters to track the session.</param>
        public DedupReadOnlyContentSession(
            IAbsFileSystem fileSystem,
            string name,
            IDedupStoreHttpClient dedupStoreHttpClient,
            TimeSpan timeToKeepContent,
            TimeSpan pinInlineThreshold,
            TimeSpan ignorePinThreshold,
            CounterTracker counterTracker = null,
            int maxConnections = DefaultMaxConnections)
            : base(name, counterTracker)
        {
            Contract.Requires(fileSystem != null);
            Contract.Requires(name != null);
            Contract.Requires(dedupStoreHttpClient != null);

            DedupStoreClient = new DedupStoreClient(dedupStoreHttpClient, DefaultMaxParallelism);
            FileSystem = fileSystem;
            TempDirectory = new DisposableDirectory(fileSystem);
            ConnectionGate = new SemaphoreSlim(maxConnections);
            EndDateTime = DateTime.UtcNow + timeToKeepContent;

            _pinInlineThreshold = pinInlineThreshold;
            _ignorePinThreshold = ignorePinThreshold;

            _counters = CounterTracker.CreateCounterCollection<BackingContentStore.SessionCounters>(counterTracker);
            _dedupCounters = CounterTracker.CreateCounterCollection<Counters>(counterTracker);
        }

        /// <summary>
        /// Dispose native resources.
        /// </summary>
        [SuppressMessage("ReSharper", "UnusedParameter.Global")]
        protected override void DisposeCore() => TempDirectory.Dispose();

        /// <inheritdoc />
        protected override Task<PinResult> PinCoreAsync(
            OperationContext context, ContentHash contentHash, UrgencyHint urgencyHint, Counter retryCounter)
        {
            return PinCoreImplAsync(context, contentHash, EndDateTime);
        }

        private async Task<PinResult> PinCoreImplAsync(
            OperationContext context, ContentHash contentHash, DateTime keepUntil)
        {
            if (!contentHash.HashType.IsValidDedup())
            {
                return new PinResult($"DedupStore client requires a HashType that supports dedup. Given hash type: {contentHash.HashType}.");
            }

            var pinResult = CheckPinInMemory(contentHash, keepUntil);
            if (pinResult.Succeeded)
            {
                return pinResult;
            }

            BlobIdentifier blobId = contentHash.ToBlobIdentifier();
            DedupIdentifier dedupId = blobId.ToDedupIdentifier();

            if (dedupId.AlgorithmId == Hashing.ChunkDedupIdentifier.ChunkAlgorithmId)
            {
                // No need to optimize since pinning a chunk is always a fast operation.
                return await PinImplAsync(context, contentHash, keepUntil);
            }

            // Since pinning the whole tree can be an expensive operation, we have optimized how we call it. Depending on the current
            //  keepUntil of the root node, which is unexpensive to check, the operation will behave differently:
            //      The pin operation will be ignored if it is greater than ignorePinThreshold, to reduce amount of calls
            //      The pin operation will be inlined if it is lower than pinInlineThreshold, to make sure that we don't try to use
            //          content that we pin in the background but has expired before we could complete the pin.
            //      The pin operation will be done asynchronously and will return success otherwise. Most calls should follow this
            //          behavior, to avoid waiting on a potentially long operation. We're confident returning a success because we
            //          know that the content is there even though we still have to extend it's keepUntil

            var keepUntilResult = await CheckNodeKeepUntilAsync(context, dedupId);
            if (!keepUntilResult.Succeeded)
            {
                // Returned a service error. Fail fast.
                return new PinResult(keepUntilResult);
            }
            else if (!keepUntilResult.Value.HasValue)
            {
                // Content not found.
                return new PinResult(PinResult.ResultCode.ContentNotFound);
            }

            var timeLeft = keepUntilResult.Value.Value - DateTime.UtcNow;


            // Make sure to only trigger this optimization for normal pins and not for pins for incorporate
            if (keepUntil == EndDateTime && timeLeft > _ignorePinThreshold)
            {
                Tracer.Debug(context, $"Pin was skipped because keepUntil has remaining time [{timeLeft}] that is greater than ignorePinThreshold=[{_ignorePinThreshold}]");
                _dedupCounters[Counters.PinIgnored].Increment();
                return PinResult.Success;
            }

            var pinTask = PinImplAsync(context, contentHash, keepUntil);

            if (timeLeft < _pinInlineThreshold)
            {
                Tracer.Debug(context, $"Pin inlined because keepUntil has remaining time [{timeLeft}] that is less than pinInlineThreshold=[{_pinInlineThreshold}]");
                _dedupCounters[Counters.PinInlined].Increment();
                return await pinTask;
            }

            pinTask.FireAndForget(context);

            return PinResult.Success;
        }

        private async Task<PinResult> PinImplAsync(OperationContext context, ContentHash contentHash, DateTime keepUntil)
        {
            try
            {
                PinResult pinResult;
                var dedupId = contentHash.ToBlobIdentifier().ToDedupIdentifier();
                if (dedupId.AlgorithmId == Hashing.ChunkDedupIdentifier.ChunkAlgorithmId)
                {
                    pinResult = await TryPinChunkAsync(context, dedupId, keepUntil);
                }
                else if (((NodeAlgorithmId)dedupId.AlgorithmId).IsValidNode())
                {
                    pinResult = await TryPinNodeAsync(context, dedupId, keepUntil);
                }
                else
                {
                    throw new InvalidOperationException($"Unknown dedup algorithm id detected for dedup {dedupId.ValueString} : {dedupId.AlgorithmId}");
                }

                if (pinResult.Succeeded)
                {
                    _counters[BackingContentStore.SessionCounters.PinSatisfiedFromRemote].Increment();
                    ExpiryCache.AddExpiry(contentHash, keepUntil);
                }

                return pinResult;
            }
            catch (Exception ex)
            {
                return new PinResult(ex);
            }
        }

        /// <inheritdoc />
        protected override async Task<OpenStreamResult> OpenStreamCoreAsync(
            OperationContext context, ContentHash contentHash, UrgencyHint urgencyHint, Counter retryCounter)
        {
            if (!contentHash.HashType.IsValidDedup())
            {
                return new OpenStreamResult($"DedupStore client requires a HashType that supports dedup. Given hash type: {contentHash.HashType}.");
            }

            string tempFile = null;
            try
            {
                tempFile = TempDirectory.CreateRandomFileName().Path;
                var result =
                    await PlaceFileInternalAsync(context, contentHash, tempFile, FileMode.Create).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    return new OpenStreamResult(new FileStream(
                        tempFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        StreamBufferSize,
                        FileOptions.DeleteOnClose));
                }

                return new OpenStreamResult(null);
            }
            catch (Exception e)
            {
                return new OpenStreamResult(e);
            }
            finally
            {
                if (tempFile != null)
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (Exception e)
                    {
                        Tracer.Warning(context, $"Error deleting temporary file at {tempFile}: {e}");
                    }
                }
            }
        }

        /// <inheritdoc />
        protected override async Task<PlaceFileResult> PlaceFileCoreAsync(
            OperationContext context,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint,
            Counter retryCount)
        {
            if (!contentHash.HashType.IsValidDedup())
            {
                return new PlaceFileResult($"DedupStore client requires a HashType that supports dedup. Given hash type: {contentHash.HashType}.");
            }

            try
            {
                if (replacementMode != FileReplacementMode.ReplaceExisting && File.Exists(path.Path))
                {
                    return PlaceFileResult.AlreadyExists;
                }

                var fileMode = replacementMode == FileReplacementMode.ReplaceExisting
                    ? FileMode.Create
                    : FileMode.CreateNew;
                var placeResult =
                    await PlaceFileInternalAsync(context, contentHash, path.Path, fileMode).ConfigureAwait(false);

                if (!placeResult.Succeeded)
                {
                    return new PlaceFileResult(placeResult, PlaceFileResult.ResultCode.NotPlacedContentNotFound);
                }

                var contentSize = GetContentSize(path);
                return PlaceFileResult.CreateSuccess(PlaceFileResult.ResultCode.PlacedWithCopy, contentSize, source: PlaceFileResult.Source.BackingStore);
            }
            catch (IOException e) when (IsErrorFileExists(e))
            {
                return PlaceFileResult.AlreadyExists;
            }
            catch (Exception e)
            {
                return new PlaceFileResult(e);
            }
        }

        /// <inheritdoc />
        protected override Task<IEnumerable<Task<Indexed<PinResult>>>> PinCoreAsync(
            OperationContext context, IReadOnlyList<ContentHash> contentHashes, UrgencyHint urgencyHint, Counter retryCounter, Counter fileCounter)
        {
            try
            {
                return Task.FromResult(contentHashes.Select(async (contentHash, i) => (await PinAsync(context, contentHash, context.Token, urgencyHint)).WithIndex(i)));
            }
            catch (Exception ex)
            {
                Tracer.Warning(context, $"Exception when querying pins against the VSTS services {ex}");
                return Task.FromResult(contentHashes.Select((_, index) => Task.FromResult(new PinResult(ex).WithIndex(index))));
            }
        }

        /// <inheritdoc />
        protected override Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceFileCoreAsync(OperationContext context, IReadOnlyList<ContentHashWithPath> hashesWithPaths, FileAccessMode accessMode, FileReplacementMode replacementMode, FileRealizationMode realizationMode, UrgencyHint urgencyHint, Counter retryCounter)
        {
            // Also not implemented in BlobReadOnlyContentSession.
            throw new NotImplementedException();
        }

        private Task<BoolResult> PlaceFileInternalAsync(
            OperationContext context, ContentHash contentHash, string path, FileMode fileMode)
        {
            try
            {
                return GetFileWithDedupAsync(context, contentHash, path);
            }
            catch (Exception e) when (fileMode == FileMode.CreateNew && !IsErrorFileExists(e))
            {
                try
                {
                    // Need to delete here so that a partial download doesn't run afoul of FileReplacementMode.FailIfExists upon retry
                    // Don't do this if the error itself was that the file already existed
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Tracer.Warning(context, $"Error deleting file at {path}: {ex}");
                }

                throw;
            }
            catch (StorageException storageEx) when (storageEx.InnerException is WebException)
            {
                var webEx = (WebException)storageEx.InnerException;
                if (((HttpWebResponse)webEx.Response).StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                throw;
            }
        }

        private async Task<BoolResult> GetFileWithDedupAsync(OperationContext context, ContentHash contentHash, string path)
        {
            BlobIdentifier blobId = contentHash.ToBlobIdentifier();
            DedupIdentifier dedupId = blobId.ToDedupIdentifier();

            try
            {
                await TryGatedArtifactOperationAsync<object>(
                    context,
                    contentHash.ToString(),
                    "DownloadToFileAsync",
                    async innerCts =>
                {
                    await DedupStoreClient.DownloadToFileAsync(dedupId, path, null, null, EdgeCache.Allowed, innerCts);
                    return null;
                });
            }
            catch (NullReferenceException) // Null reference thrown when DedupIdentifier doesn't exist in VSTS.
            {
                return new BoolResult("DedupIdentifier not found.");
            }
            catch (Exception ex)
            {
                return new BoolResult(ex);
            }

            return BoolResult.Success;
        }

        private bool IsErrorFileExists(Exception e) => (Marshal.GetHRForException(e) & ((1 << 16) - 1)) == ErrorFileExists;

        private PinResult CheckPinInMemory(ContentHash contentHash, DateTime keepUntil)
        {
            // TODO: allow cached expiry time to be within some bump threshold (e.g. allow expiryTime = 6 days & endDateTime = 7 days) (bug 1365340)
            if (ExpiryCache.TryGetExpiry(contentHash, out var expiryTime) && expiryTime > keepUntil)
            {
                _counters[BackingContentStore.SessionCounters.PinSatisfiedInMemory].Increment();
                return PinResult.Success;
            }

            return PinResult.ContentNotFound;
        }

        /// <nodoc />
        protected long GetContentSize(AbsolutePath path)
        {
            var fileInfo = new FileInfo(path.Path);
            return fileInfo.Length;
        }

        /// <summary>
        /// Because pinning requires recursing an entire tree, we need to limit the number of simultaneous calls to DedupStore.
        /// </summary>
        protected async Task<TResult> TryGatedArtifactOperationAsync<TResult>(
            OperationContext context, string content, string operationName, Func<CancellationToken, Task<TResult>> func, [CallerMemberName] string caller = null)
        {
            var sw = Stopwatch.StartNew();
            await ConnectionGate.WaitAsync(context.Token);

            var elapsed = sw.Elapsed;

            if (elapsed.TotalSeconds >= MinLogWaitTimeInSeconds)
            {
                Tracer.Warning(context, $"Operation '{caller}' for {content} was throttled for {elapsed.TotalSeconds}sec");
            }

            try
            {
                return await ArtifactHttpClientErrorDetectionStrategy.ExecuteWithTimeoutAsync(
                    context,
                    operationName,
                    innerCts => func(innerCts),
                    context.Token);
            }
            finally
            {
                ConnectionGate.Release();
            }
        }

        #region Internal Pin Methods
        /// <summary>
        /// Updates expiry of single chunk in DedupStore if it exists.
        /// </summary>
        private async Task<PinResult> TryPinChunkAsync(OperationContext context, DedupIdentifier dedupId, DateTime keepUntil)
        {
            try
            {
                var receipt = await TryGatedArtifactOperationAsync(
                    context,
                    dedupId.ValueString,
                    "TryKeepUntilReferenceChunk",
                    innerCts => DedupStoreClient.Client.TryKeepUntilReferenceChunkAsync(dedupId.CastToChunkDedupIdentifier(), new KeepUntilBlobReference(keepUntil), innerCts));

                if (receipt == null)
                {
                    return PinResult.ContentNotFound;
                }

                return PinResult.Success;
            }
            catch (Exception ex)
            {
                return new PinResult(ex);
            }
        }

        /// <summary>
        /// Updates expiry of single node in DedupStore if
        ///     1) Node exists
        ///     2) All children exist and have sufficient TTL
        /// If children have insufficient TTL, attempt to extend the expiry of all children before pinning.
        /// </summary>
        private async Task<PinResult> TryPinNodeAsync(OperationContext context, DedupIdentifier dedupId, DateTime keepUntil)
        {
            TryReferenceNodeResponse referenceResult;
            try
            {
                referenceResult = await TryGatedArtifactOperationAsync(
                    context,
                    dedupId.ValueString,
                    "TryKeepUntilReferenceNode",
                    innerCts => DedupStoreClient.Client.TryKeepUntilReferenceNodeAsync(dedupId.CastToNodeDedupIdentifier(), new KeepUntilBlobReference(keepUntil), null, innerCts));
            }
            catch (DedupNotFoundException)
            {
                // When VSTS processes response, throws exception when node doesn't exist.
                referenceResult = new TryReferenceNodeResponse(new DedupNodeNotFound());
            }
            catch (Exception ex)
            {
                return new PinResult(ex);
            }

            var pinResult = PinResult.ContentNotFound;

            referenceResult.Match(
                (notFound) =>
                {
                    // Root node has expired.
                },
                (needAction) =>
                {
                    pinResult = TryPinChildrenAsync(context, dedupId, needAction.InsufficientKeepUntil, keepUntil).GetAwaiter().GetResult();
                },
                (added) =>
                {
                    pinResult = PinResult.Success;
                });

            return pinResult;
        }

        /// <summary>
        /// Attempt to update expiry of all children. Pin parent node if all children were extended successfully.
        /// </summary>
        private async Task<PinResult> TryPinChildrenAsync(OperationContext context, DedupIdentifier parentNode, IEnumerable<DedupIdentifier> dedupIdentifiers, DateTime keepUntil)
        {
            var chunks = new List<DedupIdentifier>();
            var nodes = new List<DedupIdentifier>();

            foreach (var id in dedupIdentifiers)
            {
                if (id.AlgorithmId == Hashing.ChunkDedupIdentifier.ChunkAlgorithmId)
                {
                    chunks.Add(id);
                }
                else if (((NodeAlgorithmId)id.AlgorithmId).IsValidNode())
                {
                    nodes.Add(id);
                }
                else
                {
                    throw new InvalidOperationException($"Unknown dedup algorithm id detected for dedup {id.ValueString} : {id.AlgorithmId}");
                }
            }

            // Attempt to save all children.
            Tracer.Debug(context, $"Pinning children: nodes=[{string.Join(",", nodes.Select(x => x.ValueString))}] chunks=[{string.Join(",", chunks.Select(x => x.ValueString))}]");
            var result = await TryPinNodesAsync(context, nodes, keepUntil) & await TryPinChunksAsync(context, chunks, keepUntil);
            if (result == PinResult.Success)
            {
                // If all children are saved, pin parent.
                result = await TryPinNodeAsync(context, parentNode, keepUntil);
            }

            return result;
        }

        /// <summary>
        /// Recursively attempt to update expiry of all nodes and their children.
        /// Returns success only if all children of each node are found and extended.
        /// </summary>
        private async Task<PinResult> TryPinNodesAsync(OperationContext context, IEnumerable<DedupIdentifier> dedupIdentifiers, DateTime keepUntil)
        {
            if (!dedupIdentifiers.Any())
            {
                return PinResult.Success;
            }

            // TODO: Support batched TryKeepUntilReferenceNodeAsync in Artifact. (bug 1428612)
            var tryReferenceActionQueue = new ActionQueue(DefaultMaxParallelism);
            var pinResults = await tryReferenceActionQueue.SelectAsync(
                dedupIdentifiers,
                (dedupId, _) => TryPinNodeAsync(context, dedupId, keepUntil));

            foreach (var result in pinResults)
            {
                if (!result.Succeeded)
                {
                    return result; // An error updating one of the nodes or its children occured. Fail fast.
                }
            }

            return PinResult.Success;
        }

        /// <summary>
        /// Update all chunks if they exist. Returns success only if all chunks are found and extended.
        /// </summary>
        private async Task<PinResult> TryPinChunksAsync(OperationContext context, IEnumerable<DedupIdentifier> dedupIdentifiers, DateTime keepUntil)
        {
            if (!dedupIdentifiers.Any())
            {
                return PinResult.Success;
            }

            // TODO: Support batched TryKeepUntilReferenceChunkAsync in Artifact. (bug 1428612)
            var tryReferenceActionQueue = new ActionQueue(DefaultMaxParallelism);
            var pinResults = await tryReferenceActionQueue.SelectAsync(
                dedupIdentifiers,
                (dedupId, _) => TryPinChunkAsync(context, dedupId, keepUntil));

            foreach (var result in pinResults)
            {
                if (!result.Succeeded)
                {
                    return result; // An error updating one of the chunks occured. Fail fast.
                }
            }

            return PinResult.Success;
        }

        /// <summary>
        ///     Checks the current keepUntil of a node. Returns null if the node is not found.
        /// </summary>
        protected async Task<Result<DateTime?>> CheckNodeKeepUntilAsync(OperationContext context, DedupIdentifier dedupId)
        {
            TryReferenceNodeResponse referenceResult;
            try
            {
                // Pinning with keepUntil of now means that, if the content is available, the call will always succeed.
                referenceResult = await TryGatedArtifactOperationAsync(
                    context,
                    dedupId.ValueString,
                    "TryKeepUntilReferenceNode",
                    innerCts => DedupStoreClient.Client.TryKeepUntilReferenceNodeAsync(dedupId.CastToNodeDedupIdentifier(), new KeepUntilBlobReference(DateTime.UtcNow), null, innerCts));
            }
            catch (Exception ex)
            {
                return new Result<DateTime?>(ex);
            }

            DateTime? keepUntil = null;
            referenceResult.Match(
                (notFound) => { /* Do nothing */ },
                (needAction) =>
                {
                    // For the reason explained above, this case where children need to be pinned should never happen.
                    // However, as a best approximation, we take the min of all the children, which always outlive the parent.
                    keepUntil = needAction.Receipts.Select(r => r.Value.KeepUntil.KeepUntil).Min();
                },
                (added) =>
                {
                    keepUntil = added.Receipts[dedupId].KeepUntil.KeepUntil;
                });

            return new Result<DateTime?>(keepUntil, isNullAllowed: true);
        }

        #endregion

        /// <inheritdoc />
        protected override CounterSet GetCounters()
        {
            return base.GetCounters()
                .Merge(_counters.ToCounterSet())
                .Merge(_dedupCounters.ToCounterSet());
        }

        /// <summary>
        /// Pin content with a specific keep until.
        /// </summary>
        public Task<IEnumerable<Task<PinResult>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes, DateTime keepUntil)
        {
            try
            {
                return Task.FromResult(contentHashes.Select(async contentHash => await pinContent(context, contentHash, keepUntil)));
            }
            catch (Exception ex)
            {
                Tracer.Warning(context, $"Exception when querying pins against the VSTS services {ex}");
                return Task.FromResult(contentHashes.Select((_, index) => Task.FromResult(new PinResult(ex))));
            }

            Task<PinResult> pinContent(OperationContext operationContext, ContentHash hash, DateTime keepUntil)
            {
                return operationContext.PerformOperationAsync(
                    Tracer,
                    () => PinCoreImplAsync(operationContext, hash, keepUntil),
                    traceOperationStarted: TraceOperationStarted,
                    traceOperationFinished: TracePinFinished,
                    traceErrorsOnly: TraceErrorsOnly,
                    extraEndMessage: _ => $"input=[{hash.ToShortString()}], keepUntil=[{keepUntil}]",
                    counter: BaseCounters[ContentSessionBaseCounters.Pin]);
            }
        }
    }
}
