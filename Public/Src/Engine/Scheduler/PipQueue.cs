// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Scheduler.WorkDispatcher;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tracing;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// A dispatcher queue which processes work items from several priority queues inside.
    /// </summary>
    public sealed class PipQueue : IPipQueue
    {
        /// <summary>
        /// Priority queues by kind
        /// </summary>
        private readonly Dictionary<DispatcherKind, DispatcherQueue> m_queuesByKind;

        private readonly ChooseWorkerQueue m_chooseWorkerCpuQueue;

        /// <summary>
        /// Task completion source that completes whenever there are applicable changes which require another dispatching iteration.
        /// </summary>
        private readonly ManualResetEventSlim m_hasAnyChange;

        /// <summary>
        /// Task completion source that completes if the cancellation is requested and there are no running pips.
        /// </summary>
        private TaskCompletionSource<bool> m_hasAnyRunning;

        /// <summary>
        /// How many work items there are in the dispatcher as pending or actively running.
        /// </summary>
        private long m_numRunningOrQueued;

        /// <inheritdoc/>
        public long NumRunningOrQueuedOrRemote => Volatile.Read(ref m_numRunningOrQueued) + NumRemoteRunning;

        /// <summary>
        /// How many pips are currently executed on remote workers.
        /// </summary>
        private int m_numRemoteRunning;

        /// <inheritdoc/>
        public int NumRemoteRunning => Volatile.Read(ref m_numRemoteRunning);

        /// <summary>
        /// Whether the queue can accept new external work items.
        /// </summary>
        /// <remarks>
        /// In distributed builds, new work items can come from external requests after draining is started (i.e., workers get requests from the orchestrator)
        /// In single machine builds, after draining is started, new work items are only scheduled from the items that are being executed, not external requests.
        /// </remarks>
        private volatile bool m_isFinalized;

        /// <summary>
        /// Whether the queue is cancelled via Ctrl-C
        /// </summary>
        private bool m_isCancelled;

        private bool IsCancelled
        {
            get
            {
                return Volatile.Read(ref m_isCancelled);
            }
            set
            {
                Volatile.Write(ref m_isCancelled, value);
            }
        }

        /// <summary>
        /// The total number of process slots in the build.
        /// </summary>
        /// <remarks>
        /// For the orchestrator, this is a sum of process slots of all available workers.
        /// For a worker, this is the number of process slots on that worker.
        /// </remarks>
        private int m_totalProcessSlots;

        /// <inheritdoc/>
        public int MaxProcesses => m_queuesByKind[DispatcherKind.CPU].MaxParallelDegree;

        /// <inheritdoc/>
        public int NumSemaphoreQueued => m_queuesByKind[DispatcherKind.ChooseWorkerCpu].NumRunningPips + m_queuesByKind[DispatcherKind.ChooseWorkerCpu].NumQueued;

        /// <inheritdoc/>
        public bool IsDraining { get; private set; }

        /// <summary>
        /// Whether the queue has been completely drained
        /// </summary>
        /// <returns>
        /// If there are no items running or pending in the queues, we need to check whether this pipqueue can accept new external work.
        /// If this is a worker, we cannot finish dispatcher because orchestrator can still send new work items to the worker.
        /// </returns>
        public bool IsFinished => IsCancelled || (NumRunningOrQueuedOrRemote == 0 && m_isFinalized);

        /// <inheritdoc/>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// See <see cref="ChooseWorkerQueue.FastChooseNextCount"/>
        /// </summary>
        internal long ChooseQueueFastNextCount => m_chooseWorkerCpuQueue.FastChooseNextCount;

        /// <summary>
        /// Run time of tasks in choose worker queue
        /// </summary>
        internal TimeSpan ChooseQueueRunTime => m_chooseWorkerCpuQueue.RunTime;

        private long m_triggerDispatcherCount;
        private long m_dispatcherLoopCount;
        private TimeSpan m_dispatcherLoopTime;

        /// <summary>
        /// Time spent in dispatcher loop
        /// </summary>
        public TimeSpan DispatcherLoopTime
        {
            get
            {
                return m_dispatcherLoopTime;
            }
        }

        /// <summary>
        /// Number of dispatcher loop iterations
        /// </summary>
        public long DispatcherIterations
        {
            get
            {
                return m_dispatcherLoopCount;
            }
        }

        /// <summary>
        /// Number of times dispatcher loop was triggered
        /// </summary>
        public long TriggerDispatcherCount
        {
            get
            {
                return m_triggerDispatcherCount;
            }
        }

        private readonly IScheduleConfiguration m_scheduleConfig;

        /// <summary>
        /// Creates instance
        /// </summary>
        public PipQueue(LoggingContext loggingContext, IConfiguration config)
        {
            Contract.Requires(config != null);

            m_scheduleConfig = config.Schedule;

            // If adaptive IO is enabled, then start with the half of the maxIO.
            var ioLimit = m_scheduleConfig.AdaptiveIO ? (m_scheduleConfig.MaxIO + 1) / 2 : m_scheduleConfig.MaxIO;

            m_chooseWorkerCpuQueue = m_scheduleConfig.ModuleAffinityEnabled() ?
                new NestedChooseWorkerQueue(this, m_scheduleConfig.MaxChooseWorkerCpu, config.Distribution.RemoteWorkerCount + 1) :
                new ChooseWorkerQueue(this, m_scheduleConfig.MaxChooseWorkerCpu);

            m_queuesByKind = new Dictionary<DispatcherKind, DispatcherQueue>()
                             {
                                 {DispatcherKind.IO, new DispatcherQueue(this, ioLimit)},
                                 {DispatcherKind.DelayedCacheLookup, new DispatcherQueue(this, 1)},
                                 {DispatcherKind.ChooseWorkerCacheLookup, new DispatcherQueue(this, m_scheduleConfig.MaxChooseWorkerCacheLookup)},
                                 {DispatcherKind.ChooseWorkerLight, new DispatcherQueue(this, m_scheduleConfig.MaxChooseWorkerLight)},
                                 {DispatcherKind.ChooseWorkerIpc, new DispatcherQueue(this, 1)},
                                 {DispatcherKind.ChooseWorkerCpu, m_chooseWorkerCpuQueue},
                                 {DispatcherKind.CacheLookup, new DispatcherQueue(this, m_scheduleConfig.MaxCacheLookup)},
                                 {DispatcherKind.CPU, new DispatcherQueue(this, m_scheduleConfig.EffectiveMaxProcesses, useWeight: true)},
                                 {DispatcherKind.Materialize, new DispatcherQueue(this, m_scheduleConfig.MaxMaterialize)},
                                 {DispatcherKind.Light, new DispatcherQueue(this, m_scheduleConfig.MaxLight)},
                                 {DispatcherKind.IpcPips, new DispatcherQueue(this, m_scheduleConfig.MaxIpc)}
                             };

            m_hasAnyChange = new ManualResetEventSlim(initialState: true /* signaled */);

            Tracing.Logger.Log.PipQueueConcurrency(
                loggingContext,
                ioLimit,
                m_scheduleConfig.MaxChooseWorkerCpu,
                m_scheduleConfig.MaxChooseWorkerCacheLookup,
                m_scheduleConfig.MaxChooseWorkerLight,
                m_scheduleConfig.MaxCacheLookup,
                m_scheduleConfig.EffectiveMaxProcesses,
                m_scheduleConfig.MaxMaterialize,
                m_scheduleConfig.MaxLight,
                m_scheduleConfig.MaxIpc,
                m_scheduleConfig.OrchestratorCpuMultiplier.ToString());
        }

        /// <inheritdoc/>
        public int GetNumRunningPipsByKind(DispatcherKind kind) => m_queuesByKind[kind].NumRunningPips;

        /// <inheritdoc/>
        public int GetNumAcquiredSlotsByKind(DispatcherKind kind) => m_queuesByKind[kind].NumAcquiredSlots;

        /// <inheritdoc/>
        public int GetNumQueuedByKind(DispatcherKind kind) => m_queuesByKind[kind].NumQueued;

        /// <inheritdoc/>
        public bool IsUseWeightByKind(DispatcherKind kind) => m_queuesByKind[kind].UseWeight;

        /// <inheritdoc/>
        public int GetMaxParallelDegreeByKind(DispatcherKind kind) => m_queuesByKind[kind].MaxParallelDegree;

        /// <summary>
        /// Sets the max parallelism for the given queue
        /// </summary>
        public void SetMaxParallelDegreeByKind(DispatcherKind kind, int maxParallelDegree)
        {
            if (maxParallelDegree == 0)
            {
                // We only allow 0 for ChooseWorkerCpu and ChooseWorkerCacheLookup dispatchers.
                // For all other dispatchers, 0 is not allowed for potential deadlocks.
                if (!kind.IsChooseWorker())
                {
                    maxParallelDegree = 1;
                }
            }

            if (m_queuesByKind[kind].AdjustParallelDegree(maxParallelDegree) && maxParallelDegree > 0)
            {
                TriggerDispatcher();
            }
        }

        /// <summary>
        /// Drains the priority queues inside.
        /// </summary>
        /// <remarks>
        /// Returns a task that completes when queue is fully drained
        /// </remarks>
        public void DrainQueues()
        {
            Contract.Requires(!IsDraining, "PipQueue has already started draining.");
            Contract.Requires(!IsDisposed);
            IsDraining = true;

            while (!IsFinished)
            {
                var startTime = TimestampUtilities.Timestamp;
                Interlocked.Increment(ref m_dispatcherLoopCount);

                m_hasAnyChange.Reset();

                if (m_scheduleConfig.DelayedCacheLookupEnabled())
                {
                    int totalSlots = m_totalProcessSlots;
                    int minElements = (int)(totalSlots * m_scheduleConfig.DelayedCacheLookupMinMultiplier.Value);
                    int maxElements = (int)(totalSlots * m_scheduleConfig.DelayedCacheLookupMaxMultiplier.Value);
                    
                    Contract.Assert(minElements <= maxElements);                    
                    
                    if (m_chooseWorkerCpuQueue.NumProcessesQueued > maxElements)
                    {
                        // we have enough pips queued, pause adding new pips
                        m_queuesByKind[DispatcherKind.DelayedCacheLookup].AdjustParallelDegree(0);
                    }
                    else if (m_chooseWorkerCpuQueue.NumProcessesQueued <= minElements)
                    {
                        // not enough pips are in the queue, start adding the pips
                        m_queuesByKind[DispatcherKind.DelayedCacheLookup].AdjustParallelDegree(1);
                    }
                }

                foreach (var queue in m_queuesByKind.Values)
                {
                    queue.StartTasks();
                }

                m_dispatcherLoopTime += TimestampUtilities.Timestamp - startTime;

                // We run another iteration if at least one of these is true:
                // (1) An item has been completed.
                // (2) A new item is added to the queue: Enqueue(...) is called.
                // (3) If there is no pip running or queued.
                // (4) When you change the limit of one of the queues.
                // (5) Cancelling pip

                if (!IsFinished)
                {
                    m_hasAnyChange.Wait();
                }
            }

            if (IsCancelled)
            {
                Contract.Assert(m_hasAnyRunning != null, "If cancellation is requested, the taskcompletionsource to keep track of running items cannot be null");

                if (m_queuesByKind.Sum(a => a.Value.NumRunningPips) != 0)
                {
                    // Make sure that all running tasks are completed.
                    m_hasAnyRunning.Task.Wait();
                }
            }

            IsDraining = false;
        }

        /// <inheritdoc/>
        public void Enqueue(RunnablePip runnablePip)
        {
            Contract.Requires(runnablePip != null);
            Contract.Assert(runnablePip.DispatcherKind != DispatcherKind.None, "RunnablePip should be associated with a dispatcher kind when it is enqueued");

            if (IsCancelled)
            {
                // If the cancellation is requested, do not enqueue.
                return;
            }

            m_queuesByKind[runnablePip.DispatcherKind].Enqueue(runnablePip);

            Interlocked.Increment(ref m_numRunningOrQueued);

            // Let the dispatcher know that there is a new work item enqueued.
            TriggerDispatcher();
        }

        /// <summary>
        /// Finalizes the dispatcher so that external work will not be scheduled
        /// </summary>
        /// <remarks>
        /// Pips that already exist in the queue can still schedule their dependents after we call this method.
        /// This method allows dispatcher to stop draining when there are no pips running or queued.
        /// </remarks>
        public void SetAsFinalized()
        {
            m_isFinalized = true;
            TriggerDispatcher();
        }

        /// <summary>
        /// Adjusts the max parallel degree of the IO dispatcher queue
        /// </summary>
        /// <remarks>
        /// We introduce another limit for the IO queue, which is 'currentMax'. CurrentMax specifies the max parallel degree for the IO queue.
        /// CurrentMax initially equals to (maxIO + 1)/2. Then, based on the machine resources, it will vary between 1 and maxIO (both inclusive) during the build.
        /// This method will be called every second to adjust the IO limit.
        /// </remarks>
        public void AdjustIOParallelDegree(PerformanceCollector.MachinePerfInfo machinePerfInfo)
        {
            if (!IsDraining || !m_scheduleConfig.AdaptiveIO)
            {
                return;
            }

            var ioDispatcher = m_queuesByKind[DispatcherKind.IO];
            int currentMax = ioDispatcher.MaxParallelDegree;

            // If numRunning is closer to the currentMax, consider increasing the limit based on the resource usage
            // We should not only look at the disk usage activity but also CPU, RAM as well because the pips running on the IO queue consume CPU and RAM resources as well.
            // TODO: Instead of looking at all disk usages, just look at the ones which are associated with the build files (both inputs and outputs).
            bool hasLowGlobalUsage = machinePerfInfo.CpuUsagePercentage < 90 &&
                                     machinePerfInfo.RamUsagePercentage < 90 &&
                                     machinePerfInfo.DiskUsagePercentages.All(a => a < 90);
            bool numRunningIsNearMax = ioDispatcher.NumAcquiredSlots > currentMax * 0.8;

            if (numRunningIsNearMax && (currentMax < m_scheduleConfig.MaxIO) && hasLowGlobalUsage)
            {
                // The new currentMax will be the midpoint of currentMax and absoluteMax.
                currentMax = (m_scheduleConfig.MaxIO + currentMax + 1) / 2;

                ioDispatcher.AdjustParallelDegree(currentMax);
                TriggerDispatcher(); // After increasing the limit, trigger the dispatcher so that we can start new tasks.
            }
            else if (machinePerfInfo.DiskUsagePercentages.Any(a => a > 95))
            {
                // If any of the disks usage is higher than 95%, then decrease the limit.
                // TODO: Should we look at the CPU or MEM usage as well to decrease the limit?
                currentMax = (currentMax + 1) / 2;
                ioDispatcher.AdjustParallelDegree(currentMax);
            }

            // TODO: Right now, we only care about the disk active time. We should also take the avg disk queue length into account.
            // Average disk queue length is a product of disk transfers/sec (response X I/O) and average disk sec/transfer.
        }

        /// <inheritdoc />
        public void Cancel()
        {
            m_hasAnyRunning = new TaskCompletionSource<bool>();
            IsCancelled = true;
            TriggerDispatcher();
        }

        /// <inheritdoc />
        public async Task RemoteAsync(RunnablePip runnablePip)
        {
            if (runnablePip.IsRemotelyExecuting)
            {
                // If it is already remotely executing, there is no need to fork it on another thread.
                await runnablePip.RunAsync();
            }
            else
            {
                runnablePip.IsRemotelyExecuting = true;
                Interlocked.Increment(ref m_numRemoteRunning);
                await Task.Yield();
                await runnablePip.RunAsync();
                Interlocked.Decrement(ref m_numRemoteRunning);
                TriggerDispatcher();
            }
        }

        /// <summary>
        /// Disposes the dispatcher
        /// </summary>
        public void Dispose()
        {
            if (!IsDisposed)
            {
                foreach (var queue in m_queuesByKind.Values)
                {
                    queue.Dispose();
                }
            }

            IsDisposed = true;
        }

        internal void DecrementRunningOrQueuedPips()
        {
            Interlocked.Decrement(ref m_numRunningOrQueued);

            // if cancellation is requested, check the number of running pips.
            if (m_hasAnyRunning != null && m_queuesByKind.Sum(a => a.Value.NumRunningPips) == 0)
            {
                m_hasAnyRunning.TrySetResult(true);
            }

            TriggerDispatcher();
        }

        internal void TriggerDispatcher()
        {
            Interlocked.Increment(ref m_triggerDispatcherCount);
            m_hasAnyChange.Set();
        }

        /// <inheritdoc />
        public void SetTotalProcessSlots(int totalProcessSlots)
        {
            Interlocked.Exchange(ref m_totalProcessSlots, totalProcessSlots);
        }
    }
}
