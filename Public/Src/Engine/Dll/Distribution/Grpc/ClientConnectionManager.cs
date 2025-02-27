// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Distribution.Grpc;
using BuildXL.Engine.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;
using Grpc.Core;
using static BuildXL.Engine.Distribution.RemoteWorker;
using BuildXL.Cache.ContentStore.Grpc;

#if NET6_0_OR_GREATER
using Grpc.Net.Client.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
#endif

namespace BuildXL.Engine.Distribution.Grpc
{
    /// <nodoc/>
    internal sealed class ClientConnectionManager
    {
        public class ConnectionFailureEventArgs : EventArgs
        {
            /// <nodoc />
            public ConnectionFailureType Type { get; init; }
            
            /// <nodoc />
            public string Details { get; init; }

            /// <nodoc />
            public ConnectionFailureEventArgs(ConnectionFailureType failureType, string details)
            {
                Type = failureType;
                Details = details;
            }

            /// <summary>
            /// Log an appropriate message according to the type of connection failure
            /// </summary>
            public void Log(LoggingContext loggingContext, string machineName)
            {
                var details = Details ?? "";
                switch (Type)
                {
                    case ConnectionFailureType.CallDeadlineExceeded:
                    case ConnectionFailureType.ReconnectionTimeout:
                    case ConnectionFailureType.AttachmentTimeout:
                    case ConnectionFailureType.RemotePipTimeout:
                        Logger.Log.DistributionConnectionTimeout(loggingContext, machineName, details);
                        break;
                    case ConnectionFailureType.UnrecoverableFailure:
                        Logger.Log.DistributionConnectionUnrecoverableFailure(loggingContext, machineName, details);
                        break;
                    default:
                        Contract.Assert(false, "Unknown failure type");
                        break;
                }
            }
        }

        /// <summary>
        /// Default channel options for clients/servers to send/receive unlimited messages.
        /// </summary>
        private static readonly ChannelOption[] s_defaultChannelOptions = new ChannelOption[] { new ChannelOption(ChannelOptions.MaxSendMessageLength, int.MaxValue), new ChannelOption(ChannelOptions.MaxReceiveMessageLength, int.MaxValue) };

        public static readonly IEnumerable<ChannelOption> ServerChannelOptions = GetServerChannelOptions();

        // Verbose logging meant for debugging only
        internal static void EnableVerboseLogging(string path, GrpcEnvironmentOptions.GrpcVerbosity verbosity)
        {
#if NET6_0_OR_GREATER
            s_debugLogPathBase = path;

            // Adapt from GrpcEnvironmentOptions.GrpcVerbosity.
            // We are slightly more 'verbose' here (i.e. Debug => Trace and Error => Warning)
            // to account for the finer granularity and considering that the gRPC.NET client logging
            // is not as verbose as the Grpc.Core one.
            s_debugLogVerbosity = verbosity switch
            {
                GrpcEnvironmentOptions.GrpcVerbosity.Disabled => LogLevel.None,
                GrpcEnvironmentOptions.GrpcVerbosity.Debug => LogLevel.Trace,
                GrpcEnvironmentOptions.GrpcVerbosity.Info => LogLevel.Debug,
                GrpcEnvironmentOptions.GrpcVerbosity.Error => LogLevel.Warning,
                _ => LogLevel.Error
            };
        }
        private static string s_debugLogPathBase;
        private static LogLevel s_debugLogVerbosity;
#else
        }
#endif

        internal readonly ChannelBase Channel;

        private readonly LoggingContext m_loggingContext;
        private readonly DistributedInvocationId m_invocationId;
        private readonly Task m_monitorConnectionTask;
        public event EventHandler<ConnectionFailureEventArgs> OnConnectionFailureAsync;
        private volatile bool m_isShutdownInitiated;
        private volatile bool m_isExitCalledForServer;
        private readonly CancellationTokenSource m_exitTokenSource = new CancellationTokenSource();
        private volatile bool m_attached;
        
        private readonly bool m_dotNetClientEnabled;
        private readonly string m_ipAddress;

        /// <summary>
        /// Channel State 
        /// </summary>
#if NET6_0_OR_GREATER
        private ChannelState State => m_dotNetClientEnabled ? (ChannelState)(int)(((GrpcChannel)Channel).State) : ((Channel)Channel).State;
#else
        private ChannelState State => ((Channel)Channel).State;
#endif

        private string GenerateLog(string traceId, string status, uint numTry, string description)
        {
            // example: [MW1AAP45DD9145A] Call #1 e709c ExecutePips: 1 pips, 5 file hashes, 4F805AF2204AA5BA. 
            // example: [MW1AAP45DD9145A] Sent #1.e709c 
            // example: [MW1AAP45DD9145A] Fail #1 e709c Failure:
            string tryText = numTry != 1 ? numTry.ToString() : string.Empty;
            return $"{status}{tryText} {traceId} {description}";
        }

        private string GenerateFailLog(string traceId, uint numTry, string failure)
        {
            return GenerateLog(traceId.ToString(), "Fail", numTry, failure);
        }

        public ClientConnectionManager(LoggingContext loggingContext, string ipAddress, int port, DistributedInvocationId invocationId)
        {
            m_invocationId = invocationId;
            m_loggingContext = loggingContext;
            m_dotNetClientEnabled = EngineEnvironmentSettings.GrpcDotNetClientEnabled;
            m_ipAddress = ipAddress;

            if (m_dotNetClientEnabled)
            {
#if NET6_0_OR_GREATER
                Channel = SetupGrpcNetClient(ipAddress, port);

                if (EngineEnvironmentSettings.GrpcDotNetMonitorConnectionsEnabled)
                {
                    m_monitorConnectionTask = MonitorConnectionAsync();
                }
#endif
            }
            else
            {
                // Grpc.Core package will be deprecated in late 2022.
                Channel = SetupGrpcCoreClient(ipAddress, port);
                m_monitorConnectionTask = MonitorConnectionAsync();
            }

            Contract.Assert(Channel != null, "Channel must be initialized");
        }

        private ChannelBase SetupGrpcCoreClient(string ipAddress, int port)
        {
            var channelCreds = ChannelCredentials.Insecure;

            List<ChannelOption> channelOptions = new List<ChannelOption>();
            channelOptions.AddRange(s_defaultChannelOptions);
            if (EngineEnvironmentSettings.GrpcKeepAliveEnabled)
            {
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAlivePermitWithoutCalls, 1)); // enable sending pings
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAliveTimeMs, 300000)); // 5m-frequent pings
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAliveTimeoutMs, 60000)); // wait for 1m to receive ack for the ping before closing connection.
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MaxPingsWithoutData, 0)); // no limit for pings with no header/data
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MinSentPingIntervalWithoutDataMs, 300000)); // 5m-frequent pings with no header/data
            }

            if (GrpcSettings.EncryptionEnabled)
            {
                string certSubjectName = EngineEnvironmentSettings.CBBuildUserCertificateName;

                if (GrpcEncryptionUtils.TryGetPublicAndPrivateKeys(certSubjectName, out string publicCertificate, out string privateKey, out string hostName, out string errorMessage) &&
                    publicCertificate != null &&
                    privateKey != null &&
                    hostName != null)
                {
                    channelCreds = new SslCredentials(
                        publicCertificate,
                        new KeyCertificatePair(publicCertificate, privateKey));

                    var callCredentials = GetCallCredentialsWithToken();
                    if (callCredentials != null)
                    {
                        channelCreds = ChannelCredentials.Create(channelCreds, callCredentials);
                    }

                    // This is needed to make SSL hostname verification successful.
                    // Otherwise we see this sort of error:
                    // GrpcCore: 0 T:\src\github\grpc\workspace_csharp_ext_windows_x64\src\core\ext\filters\client_channel\subchannel.cc:1073:
                    // Connect failed: {"created":"@1628096484.767000000","description":"Peer name MW1SCH103352403 is not in peer certificate",
                    // "file":"T:\src\github\grpc\workspace_csharp_ext_windows_x64\src\core\lib\security\security_connector\ssl\ssl_security_connector.cc","file_line":59}
                    // Even though this is advertised as 'test environment' only, this is a common practice for distributed services running in a closed network.
                    channelOptions.Add(new ChannelOption(ChannelOptions.SslTargetNameOverride, hostName));

                    Logger.Log.GrpcTrace(m_loggingContext, ipAddress, "Grpc.Core auth is enabled");
                }
                else
                {
                    Logger.Log.GrpcTraceWarning(m_loggingContext, ipAddress, $"Could not extract public certificate and private key from '{certSubjectName}'. Server will be started without ssl. Error message: '{errorMessage}'");
                }
            }

            return new Channel(
                ipAddress,
                port,
                channelCreds,
                channelOptions);
        }

#if NET6_0_OR_GREATER
        private ChannelBase SetupGrpcNetClient(string ipAddress, int port)
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                ConnectTimeout = EngineEnvironmentSettings.WorkerAttachTimeout,
                Expect100ContinueTimeout = TimeSpan.Zero,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                EnableMultipleHttp2Connections = true
            };

            if (EngineEnvironmentSettings.GrpcKeepAliveEnabled)
            {
                handler.KeepAlivePingDelay = TimeSpan.FromSeconds(300); // 5m-frequent pings
                handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(60); // wait for 1m to receive ack for the ping before closing connection.
                handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;
            }

            var channelOptions = new GrpcChannelOptions
            {
                MaxSendMessageSize = int.MaxValue,
                MaxReceiveMessageSize = int.MaxValue,
                HttpHandler = handler,
            };

            if (s_debugLogPathBase != null)
            {
                // Enable logging from the client
                channelOptions.LoggerFactory = LoggerFactory.Create(l => 
                {
                    l.AddProvider(new GrpcFileLoggerAdapter(s_debugLogPathBase + $".client.{ipAddress}_{port}.grpc")); 
                    l.SetMinimumLevel(s_debugLogVerbosity); 
                });
            }
            
            if (EngineEnvironmentSettings.GrpcDotNetServiceConfigEnabled)
            {
                var defaultMethodConfig = new MethodConfig
                {
                    Names = { MethodName.Default },
                    RetryPolicy = new RetryPolicy
                    {
                        MaxAttempts = GrpcSettings.MaxAttempts,
                        InitialBackoff = TimeSpan.FromSeconds(2),
                        MaxBackoff = TimeSpan.FromSeconds(10),
                        BackoffMultiplier = 1.5,
                        RetryableStatusCodes = {                                     
                            StatusCode.Unavailable,
                            StatusCode.Internal,
                            StatusCode.Unknown }
                    }
                };

                channelOptions.ServiceConfig = new ServiceConfig { 
                    MethodConfigs = { defaultMethodConfig },
                    LoadBalancingConfigs = { new PickFirstConfig() },
                };
            }

            string address;

            if (GrpcSettings.EncryptionEnabled)
            {
                SetupChannelOptionsForEncryption(channelOptions, handler);
                address = $"https://{ipAddress}:{port}";
                Logger.Log.GrpcTrace(m_loggingContext, ipAddress, "Grpc.NET auth is enabled.");
            }
            else
            {
                AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
                address = $"http://{ipAddress}:{port}";
            }

            return GrpcChannel.ForAddress(address, channelOptions);
        }

        private void SetupChannelOptionsForEncryption(GrpcChannelOptions channelOptions, SocketsHttpHandler httpHandler)
        {
            string certSubjectName = EngineEnvironmentSettings.CBBuildUserCertificateName;

            X509Certificate2 certificate = null;

            try
            {
                certificate = GrpcEncryptionUtils.TryGetEncryptionCertificate(certSubjectName, out string error);
            }
            catch (Exception e)
            {
                Logger.Log.GrpcTraceWarning(m_loggingContext, m_ipAddress, $"An exception occurred when finding a certificate: '{e}'.");
                return;
            }

            if (certificate == null)
            {
                Logger.Log.GrpcTraceWarning(m_loggingContext, m_ipAddress, $"No certificate found that matches subject name: '{certSubjectName}'.");
                return;
            }

            httpHandler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };

            string buildUserCertificateChainsPath = EngineEnvironmentSettings.CBBuildUserCertificateChainsPath.Value;

            httpHandler.SslOptions.RemoteCertificateValidationCallback =
                (requestMessage, certificate, chain, errors) =>
            {
                if (buildUserCertificateChainsPath != null)
                {
                    if (!GrpcEncryptionUtils.TryValidateCertificate(buildUserCertificateChainsPath, chain, out string errorMessage))
                    {
                        Logger.Log.GrpcTraceWarning(m_loggingContext, m_ipAddress, $"Certificate is not validated: '{errorMessage}'.");
                        return false;
                    }
                }

                // If the path for the chains is not provided, we will not validate the certificate.
                return true;
            };

            var credentials = GetCallCredentialsWithToken();

            channelOptions.Credentials = ChannelCredentials.Create(new SslCredentials(), credentials);
        }
#endif

        private CallCredentials GetCallCredentialsWithToken()
        {
            string buildIdentityTokenLocation = EngineEnvironmentSettings.CBBuildIdentityTokenPath;

            string token = GrpcEncryptionUtils.TryGetTokenBuildIdentityToken(buildIdentityTokenLocation);

            if (token == null)
            {
                Logger.Log.GrpcTraceWarning(m_loggingContext, m_ipAddress, $"No token found in the following location: {buildIdentityTokenLocation}.");
                return null;
            }

            return CallCredentials.FromInterceptor((context, metadata) =>
            {
                if (!string.IsNullOrEmpty(token))
                {
                    metadata.Add("Authorization", token);
                }

                return Task.CompletedTask;
            });
        }

        private static IEnumerable<ChannelOption> GetServerChannelOptions()
        {
            List<ChannelOption> channelOptions = new List<ChannelOption>();
            channelOptions.AddRange(s_defaultChannelOptions);
            if (EngineEnvironmentSettings.GrpcKeepAliveEnabled)
            {
                // Pings are sent from client to server, and we do not want server to send pings to client due to the overhead concerns.
                // We just need to make server accept the pings.
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.KeepAlivePermitWithoutCalls, 1)); // enable receiving pings with no data
                channelOptions.Add(new ChannelOption(ExtendedChannelOptions.MinRecvPingIntervalWithoutDataMs, 60000)); // expecting 5m-frequent pings with no header/data. As this is minimum allowed, we state 1m pings. 
            }

            return channelOptions;
        }

        private async Task MonitorConnectionAsync()
        {
            await Task.Yield();

            ChannelState state = ChannelState.Idle;
            ChannelState lastState = state;

            var connectingStateTimer = new Stopwatch();
            
            while (state != ChannelState.Shutdown)
            {
                // We start monitoring for disconnections only after observing some 'connected' (i.e., Ready, Idle)
                // state to avoid abandoning workers that may become available some time after the build
                // starts in the orchestrator -- we will still timeout in this case but this will
                // be governed by WorkerAttachTimeout rather than DistributionConnectTimeout.
                bool monitorConnectingState = m_attached;

                try
                {
                    lastState = state;
                    if (m_dotNetClientEnabled)
                    {
#if NET6_0_OR_GREATER
                        await ((GrpcChannel)Channel).WaitForStateChangedAsync((ConnectivityState)(int)state, m_exitTokenSource.Token);
#endif
                    }
                    else
                    {
                        await ((Channel)Channel).TryWaitForStateChangedAsync(state);
                    }

                    state = State;  // Pick up the new state as soon as possible as it may change
                }
                catch (ObjectDisposedException)
                {
                    // The channel has been already shutdown and handle was disposed
                    // (https://github.com/grpc/grpc/blob/master/src/csharp/Grpc.Core/Channel.cs#L160)
                    // We shouldn't fail or leave this unobserved, instead we just stop monitoring
                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"{lastState} -> Disposed. Assuming shutdown was requested");
                    break;
                }
                catch (TaskCanceledException)
                {
                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"{lastState} -> TaskCancelledException. Assuming shutdown was requested");
                    break;
                }

                Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"{lastState} -> {state}");

                // Check if we're stuck in reconnection attemps after losing connection
                // In this situation, the state will alternate between "Connecting" and "TransientFailure"
                if (state == ChannelState.Connecting || state == ChannelState.TransientFailure)
                {
                    if (monitorConnectingState && !connectingStateTimer.IsRunning)
                    {
                        connectingStateTimer.Start();
                    }
                }
                else
                {
                    connectingStateTimer.Reset();
                }

                if (monitorConnectingState && connectingStateTimer.IsRunning && connectingStateTimer.Elapsed >= EngineEnvironmentSettings.DistributionConnectTimeout)
                {
                    OnConnectionFailureAsync?.Invoke(this, new ConnectionFailureEventArgs(ConnectionFailureType.ReconnectionTimeout, $"Timed out while the gRPC layer was trying to reconnect to the server. Timeout: {EngineEnvironmentSettings.DistributionConnectTimeout.Value.TotalMinutes} minutes"));
                    break;
                }

                // If we requested 'exit' for the server, the channel can go to 'Idle' state.
                // We should not reconnect the channel again in that case.
                if (state == ChannelState.Idle && !m_isExitCalledForServer)
                {
                    bool isReconnected = await TryReconnectAsync();
                    if (!isReconnected)
                    {
                        OnConnectionFailureAsync?.Invoke(this, new ConnectionFailureEventArgs(ConnectionFailureType.ReconnectionTimeout, "Reconnection attempts from the Idle state failed"));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Ready for exit.
        /// </summary>
        public void ReadyForExit()
        {
            // If this is an exit operation, it will make the server to exit on the other machine.
            // We need to be aware of this case as we do not want to reconnect to server. 
            m_isExitCalledForServer = true;
        }

        private async Task<bool> TryReconnectAsync()
        {
            int numRetries = 0;
            bool connectionSucceeded = false;

            while (numRetries < GrpcSettings.MaxAttempts)
            {
                numRetries++;

                // Try connecting with timeout
                connectionSucceeded = await TryConnectChannelAsync(GrpcSettings.CallTimeout, nameof(TryReconnectAsync));
                if (connectionSucceeded)
                {
                    return true;
                }
                else if (IsNonRecoverableState(State))
                {
                    // If the end state is a non-recovarable state, there is no hope for the reconnection.
                    return false;
                }
            }

            // If the connection is not established after retries, return false.
            return false;
        }

        public async Task CloseAsync()
        {
            if (!m_isShutdownInitiated)
            {
                m_isShutdownInitiated = true;

                if (m_dotNetClientEnabled)
                {
#if NET6_0_OR_GREATER
                    ((GrpcChannel)Channel).Dispose();

                    // WaitForStateChangedAsync hangs when you dispose/shutdown the channel when it is 'idle'.
                    // That's why, we pass a cancellation token to WaitForStateChangedAsync and cancel 
                    m_exitTokenSource.Cancel();
#endif
                }
                else
                {
                    await ((Channel)Channel).ShutdownAsync();
                }
            }

            if (m_monitorConnectionTask != null)
            {
                await m_monitorConnectionTask;
            }
        }

        public void OnAttachmentCompleted()
        {
            m_attached = true;
        }

        public async Task<RpcCallResult<Unit>> CallAsync(
            Func<CallOptions, AsyncUnaryCall<RpcResponse>> func,
            string operation,
            CancellationToken cancellationToken = default(CancellationToken),
            bool waitForConnection = false)
        {
            var watch = StopwatchSlim.Start();

            TimeSpan waitForConnectionDuration = TimeSpan.Zero;
            TimeSpan totalCallDuration = TimeSpan.Zero;

            if (waitForConnection)
            {
                bool connectionSucceeded = await TryConnectChannelAsync(GrpcSettings.WorkerAttachTimeout, operation, watch);
                waitForConnectionDuration = watch.Elapsed;

                if (!connectionSucceeded)
                {
                    return new RpcCallResult<Unit>(RpcCallResultState.Cancelled, attempts: 1, duration: TimeSpan.Zero, waitForConnectionDuration);
                }
            }

            var headerResult = GrpcUtils.InitializeHeaders(m_invocationId);
            string traceId = headerResult.traceId;

            RpcCallResultState state = RpcCallResultState.Succeeded;
            Failure failure = null;

            uint numTry = 0;
            var timeouts = 0;
            while (numTry < GrpcSettings.MaxAttempts)
            {
                numTry++;
                watch.ElapsedAndReset();

                try
                {
                    var callOptions = new CallOptions(
                        deadline: DateTime.UtcNow.Add(GrpcSettings.CallTimeout),
                        cancellationToken: cancellationToken,
                        headers: headerResult.headers).WithWaitForReady();

                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, GenerateLog(traceId, "Call", numTry, operation));
                    await func(callOptions);
                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, GenerateLog(traceId, "Sent", numTry, string.Empty));

                    state = RpcCallResultState.Succeeded;
                    break;
                }
                catch (RpcException e)
                {
                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, GenerateLog(traceId, "Fail", numTry, e.Message));
                    state = e.StatusCode == StatusCode.Cancelled ? RpcCallResultState.Cancelled : RpcCallResultState.Failed;
                    failure = state == RpcCallResultState.Failed ? new RecoverableExceptionFailure(new BuildXLException(e.Message)) : null;

                    if (e.Status.StatusCode == StatusCode.DeadlineExceeded)
                    {
                        timeouts++;
                    }

                    if (e.Trailers.Get(GrpcMetadata.IsUnrecoverableError)?.Value == GrpcMetadata.True)
                    {
                        OnConnectionFailureAsync?.Invoke(this, new ConnectionFailureEventArgs(ConnectionFailureType.UnrecoverableFailure, e.Status.Detail));
                        state = RpcCallResultState.Failed;
                        
                        // Unrecoverable failure - do not retry
                        break;
                    }

                    if (e.Status.StatusCode == StatusCode.InvalidArgument) 
                    {
                        if (e.Trailers.Get(GrpcMetadata.InvocationIdMismatch)?.Value == GrpcMetadata.True)
                        {
                            // The invocation ids don't match but it's not an unrecoverable error
                            // Do not retry this call because it is doomed to fail
                            state = RpcCallResultState.Failed;
                            failure = new RecoverableExceptionFailure(new BuildXLException(e.Message));
                            break;
                        }
                    }

                    // If the call is cancelled or channel is shutdown, then do not retry the call.
                    if (state == RpcCallResultState.Cancelled || m_isShutdownInitiated)
                    {
                        break;
                    }

                    if (EngineEnvironmentSettings.GrpcDotNetServiceConfigEnabled)
                    {
                        // When we use the built-in retry for grpc.net, do not retry manually.

                        timeouts = GrpcSettings.MaxAttempts;
                        break;
                    }
                }
                catch (ObjectDisposedException e)
                {
                    state = RpcCallResultState.Failed;
                    failure = new RecoverableExceptionFailure(new BuildXLException(e.Message));
                    Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, GenerateLog(traceId, "Fail", numTry, e.Message));

                    // If stream is already disposed, we cannot retry call. 
                    break;
                }
                finally
                {
                    totalCallDuration += watch.Elapsed;
                }
            }

            if (state == RpcCallResultState.Succeeded)
            {
                return new RpcCallResult<Unit>(Unit.Void, attempts: numTry, duration: totalCallDuration, waitForConnectionDuration: waitForConnectionDuration);
            }
            else if (m_attached && timeouts == GrpcSettings.MaxAttempts)
            {
                // We assume the worker is lost if we timed out every time
                OnConnectionFailureAsync?.Invoke(this,
                    new ConnectionFailureEventArgs(ConnectionFailureType.CallDeadlineExceeded,
                    $"Timed out on a call to the worker. Assuming the worker is dead. Call timeout: {GrpcSettings.CallTimeout.TotalMinutes} min. Retries: {GrpcSettings.MaxAttempts}"));
            }

            return new RpcCallResult<Unit>(
                state,
                attempts: numTry,
                duration: totalCallDuration,
                waitForConnectionDuration: waitForConnectionDuration,
                lastFailure: failure);
        }

        private async Task<bool> TryConnectChannelAsync(TimeSpan timeout, string operation, StopwatchSlim? watch = null)
        {
            watch = watch ?? StopwatchSlim.Start();
            try
            {
                Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"Connecting by {operation}");
                if (m_dotNetClientEnabled)
                {
#if NET6_0_OR_GREATER
                    CancellationTokenSource source = new CancellationTokenSource();
                    source.CancelAfter(timeout);
                    await ((GrpcChannel)Channel).ConnectAsync(source.Token);
#endif
                }
                else
                {
                    await ((Channel)Channel).ConnectAsync(DateTime.UtcNow.Add(timeout));
                }
                
                Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"Connected in {(long)watch.Value.Elapsed.TotalMilliseconds}ms");
            }
            catch (Exception e)
            {
#pragma warning disable EPC12 // Suspicious exception handling: only Message property is observed in exception block.
                Logger.Log.GrpcTrace(m_loggingContext, m_ipAddress, $"{State}. Failed to connect in {(long)watch.Value.Elapsed.TotalMilliseconds}ms. Failure {e.Message}");
#pragma warning restore EPC12 // Suspicious exception handling: only Message property is observed in exception block.

                return false;
            }

            return true;
        }

        private static bool IsNonRecoverableState(ChannelState state)
        {
            switch (state)
            {
                case ChannelState.Idle:
                case ChannelState.Shutdown:
                    return true;
                default:
                    return false;
            }
        }
    }
}