﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Monitor.App.Notifications;
using BuildXL.Cache.Monitor.App.Rules;
using BuildXL.Cache.Monitor.App.Rules.Kusto;
using BuildXL.Cache.Monitor.App.Scheduling;
using BuildXL.Cache.Monitor.Library.IcM;
using BuildXL.Cache.Monitor.Library.Notifications;
using BuildXL.Cache.Monitor.Library.Rules.Kusto;
using BuildXL.Cache.Monitor.Library.Scheduling;
using Kusto.Data.Common;
using Kusto.Ingest;

namespace BuildXL.Cache.Monitor.App
{
    public class Monitor : IDisposable
    {
        public class Configuration
        {
            public KustoCredentials KustoIngestionCredentials { get; set; } = Constants.CloudBuildProdKustoCredentials;

            public string KeyVaultUrl { get; set; } = Constants.DefaultKeyVaultUrl;

            public AzureActiveDirectoryCredentials KeyVaultCredentials { get; set; } = Constants.PMETenantCredentials;

            public string IcmUrl { get; set; } = Constants.DefaultIcmUrl;

            public Guid IcmConnectorId { get; set; } = Constants.DefaultIcmConnectorId;

            public string IcmCertificateName { get; set; } = Constants.DefaultIcmCertificateName;

            public bool ReadOnly { get; set; } = true;

            /// <summary>
            /// Which environments to monitor
            /// </summary>
            public IReadOnlyDictionary<MonitorEnvironment, EnvironmentConfiguration> Environments { get; set; } = Constants.DefaultEnvironments;

            public KustoNotifier<Notification>.Configuration KustoNotifier { get; set; } = new KustoNotifier<Notification>.Configuration
            {
                KustoDatabaseName = "CloudBuildCBTest",
                KustoTableName = "BuildXLCacheMonitor",
                KustoTableIngestionMappingName = "MonitorIngestionMapping",
            };

            public RuleScheduler.Configuration Scheduler { get; set; } = new RuleScheduler.Configuration
            {
                PersistStatePath = @"SchedulerState.json",
                PersistClearFailedEntriesOnLoad = true,
                MaximumConcurrency = new Dictionary<string, int>() {
                    // Kusto rules always perform some amount of Kusto queries. If we run too many of them at once,
                    // we'll overload the cluster and make queries fail.
                    { "Kusto", 10 },
                },
            };

            /// <summary>
            /// The scheduler writes out logs to the logger, but also writes information about rules that were run
            /// into Kusto. This is used to know when an event has passed (i.e. if a rule has been run again since
            /// the last check and didn't produce a notification, that means the event has passed).
            /// </summary>
            public KustoNotifier<RuleScheduler.LogEntry>.Configuration SchedulerKustoNotifier { get; set; } = new KustoNotifier<RuleScheduler.LogEntry>.Configuration
            {
                KustoDatabaseName = "CloudBuildCBTest",
                KustoTableName = "BuildXLCacheMonitorSchedulerLog",
                KustoTableIngestionMappingName = "MonitorIngestionMapping",
            };
        }

        private readonly Configuration _configuration;

        private readonly IClock _clock;

        private readonly ILogger _logger;

        private readonly RuleScheduler _scheduler;
        private readonly INotifier<Notification> _alertNotifier;
        private readonly INotifier<RuleScheduler.LogEntry> _schedulerLogWriter;

        private readonly IReadOnlyDictionary<MonitorEnvironment, EnvironmentResources> _environmentResources;

        private readonly IKustoIngestClient _kustoIngestClient;
        private readonly IIcmClient _icmClient;

        private static Tracer Tracer { get; } = new Tracer(nameof(Monitor));

        #region Initialization
        public static async Task<Result<Monitor>> CreateAsync(OperationContext context, Configuration configuration)
        {
            Tracer.Info(context, "Creating Kusto ingest client");
            var kustoIngestClient = ExternalDependenciesFactory.CreateKustoIngestClient(configuration.KustoIngestionCredentials).ThrowIfFailure();

            IIcmClient icmClient;
            if (!configuration.ReadOnly)
            {
                Tracer.Info(context, "Creating KeyVault client");
                var keyVaultClient = new KeyVaultClient(
                    configuration.KeyVaultUrl,
                    configuration.KeyVaultCredentials.TenantId,
                    configuration.KeyVaultCredentials.AppId,
                    configuration.KeyVaultCredentials.AppKey,
                    SystemClock.Instance,
                    Constants.IcmCertificateCacheTimeToLive);

                Tracer.Info(context, "Creating IcM client");
                icmClient = new IcmClient(
                    keyVaultClient,
                    configuration.IcmUrl,
                    configuration.IcmConnectorId,
                    configuration.IcmCertificateName,
                    SystemClock.Instance);
            }
            else
            {
                Tracer.Info(context, "Using mock ICM client");
                icmClient = new MockIcmClient();
            }

            var environmentResources = await CreateEnvironmentResourcesAsync(context, configuration.Environments);

            context.Token.ThrowIfCancellationRequested();
            return new Monitor(
                configuration,
                kustoIngestClient,
                icmClient,
                SystemClock.Instance,
                environmentResources,
                context.TracingContext.Logger);
        }

        internal static async Task<Dictionary<MonitorEnvironment, EnvironmentResources>> CreateEnvironmentResourcesAsync(OperationContext context, IReadOnlyDictionary<MonitorEnvironment, EnvironmentConfiguration> configurations)
        {
            var environmentResources = new Dictionary<MonitorEnvironment, EnvironmentResources>();

            // This does a bunch of Azure API calls, which are really slow. Making them a bit faster by doing them
            // concurrently.
            await configurations.ParallelForEachAsync(async (keyValuePair) =>
            {
                var environment = keyValuePair.Key;
                var environmentConfiguration = keyValuePair.Value;

                Tracer.Info(context, $"Loading resources for environment `{environment}`");
                var resources = await CreateEnvironmentResourcesAsync(context, environmentConfiguration);

                lock (environmentResources)
                {
                    environmentResources[environment] = resources;
                }
            });

            return environmentResources;
        }

        private static Task<EnvironmentResources> CreateEnvironmentResourcesAsync(OperationContext context, EnvironmentConfiguration configuration)
        {
            var kustoClient = ExternalDependenciesFactory.CreateKustoQueryClient(configuration.KustoCredentials).ThrowIfFailure();

            context.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new EnvironmentResources(kustoClient));
        }

        private Monitor(Configuration configuration, IKustoIngestClient kustoIngestClient, IIcmClient icmClient, IClock clock, IReadOnlyDictionary<MonitorEnvironment, EnvironmentResources> environmentResources, ILogger logger)
        {
            _configuration = configuration;

            _clock = clock;
            _logger = logger;
            _kustoIngestClient = kustoIngestClient;
            _icmClient = icmClient;
            _environmentResources = environmentResources;

            if (configuration.ReadOnly)
            {
                _alertNotifier = new LogNotifier<Notification>(_logger);
                _schedulerLogWriter = new LogNotifier<RuleScheduler.LogEntry>(_logger);
            }
            else
            {
                _alertNotifier = new KustoNotifier<Notification>(_configuration.KustoNotifier, _logger, _kustoIngestClient);
                _schedulerLogWriter = new KustoNotifier<RuleScheduler.LogEntry>(_configuration.SchedulerKustoNotifier, _logger, _kustoIngestClient);
            }

            _scheduler = new RuleScheduler(_configuration.Scheduler, _logger, _clock, _schedulerLogWriter);
        }
        #endregion

        public async Task RunAsync(OperationContext context, Action? onWatchlistChange = null)
        {
            Tracer.Info(context, "Loading stamps to watch");

            // The watchlist is essentially immutable on every run. The reason is that a change to the Watchlist
            // likely triggers a change in the scheduled rules, so we need to regenerate the entire schedule.
            var watchlist = await Watchlist.CreateAsync(
                _logger,
                _configuration.Environments,
                _environmentResources.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.KustoQueryClient));

            Tracer.Info(context, "Creating rules schedule");
            CreateSchedule(watchlist);

            if (onWatchlistChange != null)
            {
                // This rule takes care of updating the watchlist and triggering the action when it has effectively
                // changed. The action will take care of restarting the entire application.
                _scheduler.Add(
                    rule: new LambdaRule(
                        identifier: "WatchlistUpdate",
                        concurrencyBucket: "Kusto",
                        lambda: async (ruleContext) =>
                        {
                            if (await watchlist.RefreshAsync())
                            {
                                onWatchlistChange?.Invoke();
                            }
                        }),
                    pollingPeriod: TimeSpan.FromDays(1));
            }

            Tracer.Info(context, "Entering scheduler loop");
            await _scheduler.RunAsync(context.Token);
        }

        #region Schedule Generation
        /// <summary>
        /// Creates the schedule of rules that will be run. Also responsible for configuring them.
        /// </summary>
        private void CreateSchedule(Watchlist watchlist)
        {
            OncePerEnvironment(
                arguments =>
                {
                    var configuration = new LastProducedCheckpointRule.Configuration(arguments.BaseConfiguration);
                    return Analysis.Utilities.Yield(new Instantiation()
                    {
                        Rule = new LastProducedCheckpointRule(configuration),
                        PollingPeriod = TimeSpan.FromMinutes(40),
                    });
                }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new LastRestoredCheckpointRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new LastRestoredCheckpointRule(configuration),
                    PollingPeriod = TimeSpan.FromMinutes(30),
                });
            }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new MasterTakeoverRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new MasterTakeoverRule(configuration),
                    PollingPeriod = configuration.LookbackPeriod,
                });
            }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new CheckpointSizeRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new CheckpointSizeRule(configuration),
                    PollingPeriod = configuration.AnomalyDetectionHorizon - TimeSpan.FromMinutes(5),
                });
            }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new EventHubProcessingDelayRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new EventHubProcessingDelayRule(configuration),
                    PollingPeriod = configuration.LookbackPeriod,
                });
            }, watchlist);

            var failureChecks = new List<OperationFailureCheckRule.Check>() {
                new OperationFailureCheckRule.Check()
                {
                    Match = "StartupAsync",
                    Constraint = "Component != 'GrpcCopyClient'",
                },
                new OperationFailureCheckRule.Check()
                {
                    Match = "ShutdownAsync",
                },
                new OperationFailureCheckRule.Check()
                {
                    Match = "RestoreCheckpointAsync",
                },
                new OperationFailureCheckRule.Check()
                {
                    Match = "CreateCheckpointAsync",
                },
                new OperationFailureCheckRule.Check()
                {
                    Match = "ReconcileAsync",
                },
                new OperationFailureCheckRule.Check()
                {
                    Match = "ProcessEventsCoreAsync",
                },
                new OperationFailureCheckRule.Check()
                {
                    // TODO(jubayard): lower severity
                    Match = "SendEventsCoreAsync",
                },
            };

            OncePerEnvironment(arguments =>
            {
                return failureChecks.Select(check =>
                {
                    var configuration = new OperationFailureCheckRule.Configuration(arguments.BaseConfiguration, check);

                    return new Instantiation()
                    {
                        Rule = new OperationFailureCheckRule(configuration),
                        PollingPeriod = configuration.LookbackPeriod - TimeSpan.FromMinutes(5),
                    };
                });
            }, watchlist);

            var performanceChecks = new List<OperationPerformanceOutliersRule.DynamicCheck>() {
                new OperationPerformanceOutliersRule.DynamicCheck()
                {
                    LookbackPeriod = TimeSpan.FromMinutes(60),
                    DetectionPeriod = TimeSpan.FromMinutes(30),
                    Match = "LocalCacheServer.StartupAsync",
                    Constraint = $"Duration >= {CslTimeSpanLiteral.AsCslString(TimeSpan.FromMinutes(1))}",
                },
                new OperationPerformanceOutliersRule.DynamicCheck()
                {
                    LookbackPeriod = TimeSpan.FromHours(12),
                    DetectionPeriod = TimeSpan.FromHours(1),
                    Match = "CheckpointManager.CreateCheckpointAsync",
                    Constraint = $"Duration >= {CslTimeSpanLiteral.AsCslString(TimeSpan.FromMinutes(1))}",
                },
                new OperationPerformanceOutliersRule.DynamicCheck()
                {
                    LookbackPeriod = TimeSpan.FromHours(12),
                    DetectionPeriod = TimeSpan.FromHours(1),
                    Match = "CheckpointManager.RestoreCheckpointAsync",
                    Constraint = $"Duration >= P95 and P95 >= {CslTimeSpanLiteral.AsCslString(TimeSpan.FromMinutes(30))}",
                },
            };

            OncePerEnvironment(arguments =>
            {
                return performanceChecks.Select(check =>
                {
                    var configuration = new OperationPerformanceOutliersRule.Configuration(arguments.BaseConfiguration, check);

                    return new Instantiation
                    {
                        Rule = new OperationPerformanceOutliersRule(configuration),
                        PollingPeriod = check.DetectionPeriod - TimeSpan.FromMinutes(5),
                    };
                });
            }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new ServiceRestartsRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new ServiceRestartsRule(configuration),
                    PollingPeriod = TimeSpan.FromMinutes(30),
                });
            }, watchlist);

            OncePerEnvironment(arguments =>
            {
                var configuration = new LongCopyRule.Configuration(arguments.BaseConfiguration);
                return Analysis.Utilities.Yield(new Instantiation()
                {
                    Rule = new LongCopyRule(configuration),
                    PollingPeriod = TimeSpan.FromMinutes(30),
                });
            }, watchlist);
        }

        /// <summary>
        /// Schedules a rule to be run over different stamps and environments.
        /// </summary>
        private void OncePerStamp(Func<SingleStampRuleArguments, IEnumerable<Instantiation>> generator, Watchlist watchlist)
        {
            foreach (var (stampId, properties) in watchlist.Entries)
            {
                var environmentConfiguration = _configuration.Environments[stampId.Environment];
                var resources = _environmentResources[stampId.Environment];

                var configuration = new SingleStampRuleConfiguration(
                    _clock,
                    _logger,
                    _alertNotifier,
                    resources.KustoQueryClient,
                    _icmClient,
                    environmentConfiguration.KustoDatabaseName,
                    stampId);

                var request = new SingleStampRuleArguments
                {
                    StampId = stampId,
                    DynamicStampProperties = properties,
                    BaseConfiguration = configuration,
                    EnvironmentResources = resources,
                };

                foreach (var rule in generator(request))
                {
                    Contract.AssertNotNull(rule.Rule);
                    _scheduler.Add(rule.Rule, rule.PollingPeriod, rule.ForceRun);
                }
            }
        }

        /// <summary>
        /// Schedules a rule to be run over different environments.
        /// </summary>
        private void OncePerEnvironment(Func<MultiStampRuleArguments, IEnumerable<Instantiation>> generator, Watchlist watchlist)
        {
            foreach (var environment in watchlist.Entries.Select(kvp => kvp.Key.Environment).Distinct())
            {
                var resources = _environmentResources[environment];

                var configuration = new MultiStampRuleConfiguration(
                    _clock,
                    _logger,
                    _alertNotifier,
                    resources.KustoQueryClient,
                    _icmClient,
                    _configuration.Environments[environment].KustoDatabaseName,
                    environment,
                    watchlist);

                var request = new MultiStampRuleArguments
                {
                    BaseConfiguration = configuration,
                    EnvironmentResources = resources,
                };

                foreach (var rule in generator(request))
                {
                    Contract.AssertNotNull(rule.Rule);
                    _scheduler.Add(rule.Rule, rule.PollingPeriod, rule.ForceRun);
                }
            }
        }

        private class SingleStampRuleArguments
        {
            public StampId StampId { get; set; }

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
            public Watchlist.DynamicStampProperties DynamicStampProperties { get; set; }

            public SingleStampRuleConfiguration BaseConfiguration { get; set; }

            public EnvironmentResources EnvironmentResources { get; set; }
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        }

        private class MultiStampRuleArguments
        {
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
            public MultiStampRuleConfiguration BaseConfiguration { get; set; }

            public EnvironmentResources EnvironmentResources { get; set; }
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        }

        private class Instantiation
        {
            public IRule? Rule { get; set; }

            public TimeSpan PollingPeriod { get; set; }

            public bool ForceRun { get; set; } = false;
        };
        #endregion

        #region IDisposable Support
        private bool _disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _scheduler?.Dispose();

                    if (_schedulerLogWriter is IDisposable schedulerLogWriter)
                    {
                        schedulerLogWriter?.Dispose();
                    }

                    if (_alertNotifier is IDisposable alertNofier)
                    {
                        alertNofier?.Dispose();
                    }

                    _kustoIngestClient?.Dispose();

                    foreach (var (_, resources) in _environmentResources)
                    {
                        resources.Dispose();
                    }
                }

                _disposedValue = true;
            }
        }

        public void Dispose() => Dispose(true);
        #endregion
    }
}
