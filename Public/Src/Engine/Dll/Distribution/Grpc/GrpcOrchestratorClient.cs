// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Distribution.Grpc;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using Grpc.Core;
using static BuildXL.Engine.Distribution.Grpc.ClientConnectionManager;

namespace BuildXL.Engine.Distribution.Grpc
{
    /// <nodoc/>
    internal sealed class GrpcOrchestratorClient : IOrchestratorClient
    {
        private readonly DistributedInvocationId m_invocationId;
        private Orchestrator.OrchestratorClient m_client;
        private ClientConnectionManager m_connectionManager;
        private readonly LoggingContext m_loggingContext;
        private volatile bool m_initialized;

        public GrpcOrchestratorClient(LoggingContext loggingContext, DistributedInvocationId invocationId)
        {
            m_invocationId = invocationId;
            m_loggingContext = loggingContext;
        }

        public Task<RpcCallResult<Unit>> SayHelloAsync(ServiceLocation myLocation, CancellationToken cancellationToken = default)
        {
            return m_connectionManager.CallAsync(
               (callOptions) => m_client.HelloAsync(myLocation, options: callOptions),
               "Hello",
               cancellationToken: cancellationToken,
               waitForConnection: true);
        }

        public void Initialize(string ipAddress, 
            int port, 
            EventHandler<ConnectionFailureEventArgs> onConnectionFailureAsync)
        {
            m_connectionManager = new ClientConnectionManager(m_loggingContext, ipAddress, port, m_invocationId);
            m_connectionManager.OnConnectionFailureAsync += onConnectionFailureAsync;
            m_client = new Orchestrator.OrchestratorClient(m_connectionManager.Channel);
            m_initialized = true;
        }

        public Task CloseAsync()
        {
            if (!m_initialized)
            {
                return Task.CompletedTask;
            }

            return m_connectionManager.CloseAsync();
        }

        public async Task<RpcCallResult<Unit>> AttachCompletedAsync(AttachCompletionInfo message)
        {
            Contract.Assert(m_initialized);

            var attachmentCompletion = await m_connectionManager.CallAsync(
                (callOptions) => m_client.AttachCompletedAsync(message, options: callOptions),
                "AttachCompleted",
                waitForConnection: true);

            if (attachmentCompletion.Succeeded)
            {
                m_connectionManager.OnAttachmentCompleted();
            }

            return attachmentCompletion;
        }

        public Task<RpcCallResult<Unit>> ReportPipResultsAsync(PipResultsInfo message, string description, CancellationToken cancellationToken = default)
        {
            Contract.Assert(m_initialized);

            return m_connectionManager.CallAsync(
               (callOptions) => m_client.ReportPipResultsAsync(message, options: callOptions),
               description,
               cancellationToken: cancellationToken);
        }

        public Task<RpcCallResult<Unit>> ReportExecutionLogAsync(ExecutionLogInfo message, CancellationToken cancellationToken = default)
        {
            Contract.Assert(m_initialized);

            return m_connectionManager.CallAsync(
               (callOptions) => m_client.ReportExecutionLogAsync(message, options: callOptions),
               $" ReportExecutionLog: Size={message.Events.DataBlob.Count()}, SequenceNumber={message.Events.SequenceNumber}",
               cancellationToken: cancellationToken);
        }

        public AsyncClientStreamingCall<ExecutionLogInfo, RpcResponse> StreamExecutionLog(CancellationToken cancellationToken = default)
        {
            Contract.Assert(m_initialized);

            var headerResult = GrpcUtils.InitializeHeaders(m_invocationId);
            return m_client.StreamExecutionLog(headers: headerResult.headers, cancellationToken: cancellationToken);
        }

        public AsyncClientStreamingCall<PipResultsInfo, RpcResponse> StreamPipResults(CancellationToken cancellationToken = default)
        {
            Contract.Assert(m_initialized);

            var headerResult = GrpcUtils.InitializeHeaders(m_invocationId);
            return m_client.StreamPipResults(headers: headerResult.headers, cancellationToken: cancellationToken);
        }
    }
}