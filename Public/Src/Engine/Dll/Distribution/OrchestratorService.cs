// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Distribution.Grpc;
using BuildXL.Engine.Cache.Fingerprints;
using BuildXL.Engine.Tracing;
using BuildXL.Pips;
using BuildXL.Scheduler;
using BuildXL.Scheduler.Distribution;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using PipGraphCacheDescriptor = BuildXL.Engine.Cache.Fingerprints.PipGraphCacheDescriptor;

namespace BuildXL.Engine.Distribution
{
    /// <summary>
    /// Service methods called by the RPC layer as part of the RPC started in a remote worker machine
    /// </summary>
    /// <remarks>This interface is marked internal to reduce visibility to the distribution layer only</remarks>
    internal interface IOrchestratorService
    {
        void Hello(ServiceLocation workerLocation);
        void AttachCompleted(AttachCompletionInfo attachCompletionInfo);
        Task ReceivedPipResults(PipResultsInfo pipResults);
        Task ReceivedExecutionLog(ExecutionLogInfo executionLog);
    }

    /// <summary>
    /// A pip executor which can distributed work to remote workers
    /// </summary>
    public sealed class OrchestratorService : DistributionService, IOrchestratorService
    {
        internal IPipExecutionEnvironment Environment
        {
            get
            {
                Contract.Assert(m_environment != null, "Distribution must be enabled to access pip graph");
                return m_environment;
            }
        }

        internal ExecutionResultSerializer ResultSerializer
        {
            get
            {
                Contract.Assert(m_resultSerializer != null);
                return m_resultSerializer;
            }
        }

        private readonly RemoteWorker[] m_remoteWorkers;
        private readonly LoggingContext m_loggingContext;

        private IPipExecutionEnvironment m_environment;
        private ExecutionResultSerializer m_resultSerializer;
        private PipGraphCacheDescriptor m_cachedGraphDescriptor;
        private readonly ushort m_buildServicePort;
        private readonly IServer m_orchestratorServer;

        /// <summary>
        /// Class constructor
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "RemoteWorker disposes the workerClient")]
        public OrchestratorService(IDistributionConfiguration config, LoggingContext loggingContext, DistributedInvocationId invocationId, PipExecutionContext context) : base(invocationId)
        {
            Contract.Requires(config != null && config.BuildRole.IsOrchestrator());

            // Create all remote workers
            m_buildServicePort = config.BuildServicePort;
            m_remoteWorkers = new RemoteWorker[config.RemoteWorkerCount];

            m_loggingContext = loggingContext;

            for (int i = 0; i < config.RemoteWorkerCount; i++)     
            {
                ServiceLocation serviceLocation = null;
                if (i < config.BuildWorkers.Count)
                {
                    var configWorker = config.BuildWorkers[i];
                    serviceLocation = new ServiceLocation { IpAddress = configWorker.IpAddress, Port = configWorker.BuildServicePort };
                }
                else
                {
                    // This remote worker will have a location after a worker says hello
                    // at some point in the future - we don't know its service location now,
                    // so it will stay as null until we receive a Hello RPC.
                }

                var workerId = i + 1; // 0 represents the local worker.
                m_remoteWorkers[i] = new RemoteWorker(loggingContext, (uint)workerId, this, serviceLocation, context);
            }

            m_orchestratorServer = new Grpc.GrpcOrchestratorServer(loggingContext, this, invocationId);
        }

        /// <summary>
        /// The port on which the orchestrator is listening.
        /// </summary>
        public int Port => m_buildServicePort;

        /// <summary>
        /// The descriptor for the cached graph
        /// </summary>
        public PipGraphCacheDescriptor CachedGraphDescriptor
        {
            get
            {
                return m_cachedGraphDescriptor;
            }

            set
            {
                Contract.Requires(value != null);
                m_cachedGraphDescriptor = value;
            }
        }

        /// <summary>
        /// Content hash of symlink file.
        /// </summary>
        public ContentHash SymlinkFileContentHash { get; set; } = WellKnownContentHashes.AbsentFile;

        /// <summary>
        /// Prepares the orchestrator for pips execution
        /// </summary>
        public void EnableDistribution(EngineSchedule schedule)
        {
            Contract.Requires(schedule != null);

            m_environment = schedule.Scheduler;

            schedule.Scheduler.EnableDistribution(m_remoteWorkers);
            m_resultSerializer = new ExecutionResultSerializer(schedule.MaxSerializedAbsolutePath, m_environment.Context);
        }

        /// <summary>
        /// Completes the attachment of a worker.
        /// </summary>
        void IOrchestratorService.AttachCompleted(AttachCompletionInfo attachCompletionInfo)
        {
            var worker = GetWorkerById(attachCompletionInfo.WorkerId);
            worker.AttachCompletedAsync(attachCompletionInfo);
        }

        /// <summary>
        /// Handler for the 'work completion' notification from worker.
        /// </summary>
        Task IOrchestratorService.ReceivedExecutionLog(ExecutionLogInfo executionLog)
        {
            var worker = GetWorkerById(executionLog.WorkerId);
            return worker.ReadExecutionLogAsync(executionLog.Events);
        }

        /// <summary>
        /// Handler for the 'work completion' notification from worker.
        /// </summary>
        [SuppressMessage("AsyncUsage", "AsyncFixer03:FireForgetAsyncVoid", Justification = "This is eventhandler so fire&forget is understandable")]
        async Task IOrchestratorService.ReceivedPipResults(PipResultsInfo pipResults)
        {
            var worker = GetWorkerById(pipResults.WorkerId);

            if (pipResults.BuildManifestEvents?.DataBlob.Count() > 0)
            {
                // The channel is unblocked and ACK is sent after we put the execution blob to the queue in 'LogExecutionBlobAsync' method.
                await worker.ReadBuildManifestEventsAsync(pipResults.BuildManifestEvents);
            }
            else
            {
                // Return immediately to unblock the channel so that worker can receive the ACK for the sent message
                await Task.Yield();
            }

            foreach (var forwardedEvent in pipResults.ForwardedEvents)
            {
                EventLevel eventLevel = (EventLevel)forwardedEvent.Level;

                // For some errors, we need to exit the worker.
                // Those errors should not make the orchestrator fail, 
                // so we override the level with Warning.
                if (await worker.NotifyInfrastructureErrorAsync(forwardedEvent))
                {
                    eventLevel = EventLevel.Warning;
                }

                switch (eventLevel)
                {
                    case EventLevel.Error:
                        var status = worker.Status;

                        // If we receive new failures from an already stopped worker (we're not talking to it anymore), we log them as verbose events instead.
                        // This prevents logging errors for failed work that we retried elsewhere after abandoning that worker: in those cases,
                        // the build will succeed but we will complain about the logged errors and crash.
                        var shouldLogForwardedErrorAsVerbose = status == WorkerNodeStatus.Stopped;
                        Action<LoggingContext, WorkerForwardedEvent> logForwardedError =
                            shouldLogForwardedErrorAsVerbose ? Logger.Log.StoppedDistributionWorkerForwardedError : Logger.Log.DistributionWorkerForwardedError;

                        if (forwardedEvent.EventId == (int)BuildXL.Processes.Tracing.LogEventId.PipProcessError)
                        {
                            var pipProcessErrorEvent = new PipProcessErrorEventFields(
                                forwardedEvent.PipProcessErrorEvent.PipSemiStableHash,
                                forwardedEvent.PipProcessErrorEvent.PipDescription,
                                forwardedEvent.PipProcessErrorEvent.PipSpecPath,
                                forwardedEvent.PipProcessErrorEvent.PipWorkingDirectory,
                                forwardedEvent.PipProcessErrorEvent.PipExe,
                                forwardedEvent.PipProcessErrorEvent.OutputToLog,
                                forwardedEvent.PipProcessErrorEvent.MessageAboutPathsToLog,
                                forwardedEvent.PipProcessErrorEvent.PathsToLog,
                                forwardedEvent.PipProcessErrorEvent.ExitCode,
                                forwardedEvent.PipProcessErrorEvent.OptionalMessage,
                                forwardedEvent.PipProcessErrorEvent.ShortPipDescription,
                                forwardedEvent.PipProcessErrorEvent.PipExecutionTimeMs
                                );

                            logForwardedError(
                                m_loggingContext,
                                new WorkerForwardedEvent()
                                {
                                    Text = forwardedEvent.Text,
                                    WorkerName = worker.WorkerIpAddress,
                                    EventId = forwardedEvent.EventId,
                                    EventName = forwardedEvent.EventName,
                                    EventKeywords = forwardedEvent.EventKeywords,
                                    PipProcessErrorEvent = pipProcessErrorEvent,
                                });
                        } else
                        {
                            logForwardedError(
                                m_loggingContext,
                                new WorkerForwardedEvent()
                                {
                                    Text = forwardedEvent.Text,
                                    WorkerName = worker.Name,
                                    EventId = forwardedEvent.EventId,
                                    EventName = forwardedEvent.EventName,
                                    EventKeywords = forwardedEvent.EventKeywords,
                                });
                        }

                        if (!shouldLogForwardedErrorAsVerbose)
                        {
                            m_loggingContext.SpecifyErrorWasLogged((ushort)forwardedEvent.EventId);
                        }
                        break;
                    case EventLevel.Warning:
                        Logger.Log.DistributionWorkerForwardedWarning(
                            m_loggingContext,
                            new WorkerForwardedEvent()
                            {
                                Text = forwardedEvent.Text,
                                WorkerName = worker.Name,
                                EventId = forwardedEvent.EventId,
                                EventName = forwardedEvent.EventName,
                                EventKeywords = forwardedEvent.EventKeywords,
                            });
                        break;
                    default:
                        break;
                }
            }

            Parallel.ForEach(pipResults.CompletedPips, worker.NotifyPipCompletion);
        }

        private RemoteWorker GetWorkerById(uint id)
        {
            // Because 0 represents the local worker node, we need to substract 1 from the id.
            // We only store the remote workers in this class.
            return m_remoteWorkers[id - 1];
        }

        /// <inheritdoc/>
        public override void Dispose() => DisposeAsync().GetAwaiter().GetResult();

        /// <inheritdoc/>
        public async Task DisposeAsync()
        {
            LogStatistics(m_loggingContext);

            if (m_remoteWorkers != null)
            {
                var tasks = m_remoteWorkers
                    .Select(
                        static async w =>
                        {
                            using (w)
                            {
                                // Disposing the workers once FinishAsync is done.
                                await w.FinishAsync(null);
                            }
                        })
                    .ToArray();

                await TaskUtilities.SafeWhenAll(tasks);
            }

            await m_orchestratorServer.DisposeAsync();
        }

        /// <inheritdoc />
        public override bool Initialize()
        {
            // Start listening to the port if we have remote workers
            if (m_remoteWorkers.Length > 0)
            {
                try
                {
                    m_orchestratorServer.Start(m_buildServicePort);
                }
                catch (Exception ex)
                {
                    Logger.Log.DistributionServiceInitializationError(m_loggingContext, DistributedBuildRole.Orchestrator.ToString(), m_buildServicePort, ExceptionUtilities.GetLogEventMessage(ex));
                    return false;
                }
            }

            return true;
        }

        /// We don't have exit logic for orchestrator for now -- throw to avoid unexpected usage
        public override Task ExitAsync(Optional<string> failure, bool isUnexpected) => throw new NotImplementedException();

        /// <summary>
        /// A worker advertises its location during the build
        /// </summary>
        public void Hello(ServiceLocation workerLocation)
        {
            lock (m_remoteWorkers)
            {
                if (m_remoteWorkers.Any(rw => rw.Location == workerLocation))
                {
                    // We already know this worker (presumably, from the command line).
                    // Just acknowledge the RPC.
                    return;
                }

                // Choose a "dynamic" slot (with unknown location), if any, and set its service location
                var availableWorkerSlot = m_remoteWorkers.FirstOrDefault(rw => rw.Location == null);
                if (availableWorkerSlot is not null)
                {
                    availableWorkerSlot.Location = workerLocation;

                    Logger.Log.DistributionHelloReceived(m_loggingContext, workerLocation.IpAddress, workerLocation.Port, availableWorkerSlot.WorkerId);
                }
                else
                {
                    // If we receive a worker location and don't have a slot, it means that /dynamicWorkerCount had a wrong value:
                    // this is a configuration error.
                    Logger.Log.DistributionHelloNoSlot(m_loggingContext, workerLocation.IpAddress, workerLocation.Port);
                }
            }
        }
    }
}
