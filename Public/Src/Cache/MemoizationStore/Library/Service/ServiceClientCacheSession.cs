﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Sessions;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Tracing;
using BuildXL.Utilities.Tracing;

namespace BuildXL.Cache.MemoizationStore.Service
{
    /// <todoc />
    public class ServiceClientCacheSession : ServiceClientContentSession, ICacheSession, IReadOnlyMemoizationSessionWithLevelSelectors
    {
        private CounterCollection<MemoizationStoreCounters> _memoizationCounters { get; } = new CounterCollection<MemoizationStoreCounters>();

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(ServiceClientCacheSession));

        private readonly GrpcCacheClient _rpcCacheClient;

        /// <nodoc />
        public ServiceClientCacheSession(
            OperationContext context,
            string name,
            ImplicitPin implicitPin,
            ILogger logger,
            IAbsFileSystem fileSystem,
            ServiceClientContentSessionTracer sessionTracer,
            ServiceClientContentStoreConfiguration configuration,
            Func<IRpcClient> rpcClientFactory = null)
            : base(context, name, implicitPin, logger, fileSystem, sessionTracer, configuration, rpcClientFactory ?? (() => GetRpcClient(context, fileSystem, sessionTracer, configuration)))
        {
            // RpcClient is created by the base class constructor, but we know that this is the result of GetPrcClient call
            // that actually returns GrpcCacheClient instance.
            _rpcCacheClient = (GrpcCacheClient)RpcClient;
        }

        private static IRpcClient GetRpcClient(
            OperationContext context,
            IAbsFileSystem fileSystem,
            ServiceClientContentSessionTracer sessionTracer,
            ServiceClientContentStoreConfiguration configuration)
        {
            return new GrpcCacheClient(context, sessionTracer, fileSystem, configuration.RpcConfiguration, configuration.Scenario);
        }

        private Task<TResult> PerformOperationAsync<TResult>(Context context, CancellationToken cts, Func<OperationContext, GrpcCacheClient, Task<TResult>> func, [CallerMemberName]string caller = null, Counter? counter = null, Counter? retryCounter = null, bool traceErrorsOnly = false, string additionalStopMessage = null, string additionalStartMessage = null) where TResult : ResultBase
        {
            return WithOperationContext(context, cts,
                operationContext =>
                {
                    return operationContext.PerformOperationAsync(
                        Tracer,
                        () => PerformRetries(
                                operationContext,
                                () => func(operationContext, _rpcCacheClient),
                                retryCounter: retryCounter),
                        caller: caller,
                        counter: counter,
                        traceOperationStarted: TraceOperationStarted,
                        extraStartMessage: additionalStartMessage,
                        traceErrorsOnly: traceErrorsOnly,
                        extraEndMessage: additionalStopMessage == null ? (Func<TResult, string>)null : _ => additionalStopMessage);
                });
        }

        /// <inheritdoc />
        public Task<AddOrGetContentHashListResult> AddOrGetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism contentHashListWithDeterminism, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            var contentHashes = contentHashListWithDeterminism.ContentHashList.Hashes;
            return PerformOperationAsync(
                context,
                cts,
                (ctx, client) => client.AddOrGetContentHashListAsync(ctx, strongFingerprint, contentHashListWithDeterminism),
                counter: _memoizationCounters[MemoizationStoreCounters.AddOrGetContentHashList],
                retryCounter: _memoizationCounters[MemoizationStoreCounters.AddOrGetContentHashListRetries],
                additionalStartMessage: $"StrongFingerprint=({strongFingerprint}) {contentHashListWithDeterminism.ToTraceString()}",
                additionalStopMessage: $"StrongFingerprint=({strongFingerprint}) {contentHashListWithDeterminism.ToTraceString()}");
        }

        /// <inheritdoc />
        public Task<GetContentHashListResult> GetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PerformOperationAsync(
                context,
                cts,
                (ctx, client) => client.GetContentHashListAsync(ctx, strongFingerprint),
                counter: _memoizationCounters[MemoizationStoreCounters.GetContentHashList],
                retryCounter: _memoizationCounters[MemoizationStoreCounters.GetContentHashListRetries],
                additionalStartMessage: $"StrongFingerprint=({strongFingerprint})",
                additionalStopMessage: $"StrongFingerprint=({strongFingerprint})");
        }

        /// <inheritdoc />
        public System.Collections.Generic.IAsyncEnumerable<GetSelectorResult> GetSelectors(Context context, Fingerprint weakFingerprint, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            _memoizationCounters[MemoizationStoreCounters.GetSelectorsCalls].Increment();
            return this.GetSelectorsAsAsyncEnumerable(context, weakFingerprint, cts, urgencyHint);
        }

        /// <inheritdoc />
        public Task<Result<LevelSelectors>> GetLevelSelectorsAsync(Context context, Fingerprint weakFingerprint, CancellationToken cts, int level)
        {
            return PerformOperationAsync(
                context,
                cts,
                (ctx, client) => client.GetLevelSelectorsAsync(ctx, weakFingerprint, level),
                counter: _memoizationCounters[MemoizationStoreCounters.GetLevelSelectors],
                retryCounter: _memoizationCounters[MemoizationStoreCounters.GetLevelSelectorsRetries],
                additionalStopMessage: $"WeakFingerprint=[{weakFingerprint}]");
        }

        /// <inheritdoc />
        public Task<BoolResult> IncorporateStrongFingerprintsAsync(Context context, IEnumerable<Task<StrongFingerprint>> strongFingerprints, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        {
            return PerformOperationAsync(
                context,
                cts,
                (ctx, client) => client.IncorporateStrongFingerprintsAsync(ctx, strongFingerprints),
                counter: _memoizationCounters[MemoizationStoreCounters.IncorporateStrongFingerprints],
                retryCounter: _memoizationCounters[MemoizationStoreCounters.IncorporateStrongFingerprintsRetries]);
        }

        /// <inheritdoc />
        protected override CounterSet GetCounters() {
            var counters = new CounterSet();
            counters.Merge(base.GetCounters());
            counters.Merge(_memoizationCounters.ToCounterSet());
            return counters;
        }
    }
}
