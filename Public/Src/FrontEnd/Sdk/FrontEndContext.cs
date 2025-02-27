// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Threading;
using BuildXL.FrontEnd.Sdk.FileSystem;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Qualifier;
using BuildXL.Utilities.Tracing;

namespace BuildXL.FrontEnd.Sdk
{
    /// <summary>
    /// Context needed for FrontEnds
    /// </summary>
    public sealed class FrontEndContext : PipExecutionContext
    {
        /// <nodoc />
        public readonly LoggingContext LoggingContext;

        /// <nodoc />
        public IFileSystem FileSystem { get; private set; }

        /// <summary>
        /// PipData builder pool
        /// </summary>
        public readonly ObjectPool<PipDataBuilder> PipDataBuilderPool;

        /// <nodoc />
        public CredentialScanner CredentialScanner { get; }

        /// <summary>
        /// Helper to get a pipDataBuilder
        /// </summary>
        public PooledObjectWrapper<PipDataBuilder> GetPipDataBuilder() => this.PipDataBuilderPool.GetInstance();

        /// <nodoc />
        private FrontEndContext(PathTable pathTable, SymbolTable symbolTable, QualifierTable qualifierTable, LoggingContext loggingContext, IFileSystem fileSystem, IFrontEndConfiguration frontEndConfig, CancellationToken cancellationToken)
            : base(cancellationToken, pathTable.StringTable, pathTable, symbolTable, qualifierTable)
        {
            Contract.Requires(pathTable != null);
            Contract.Requires(symbolTable != null);
            Contract.Requires(loggingContext != null);
            Contract.Requires(fileSystem != null);
            Contract.Requires(qualifierTable != null);
            Contract.Requires(pathTable.StringTable == symbolTable.StringTable);
            Contract.Requires(pathTable.StringTable ==  qualifierTable.StringTable);

            LoggingContext = loggingContext;
            FileSystem = fileSystem;
            CredentialScanner = new CredentialScanner(frontEndConfig, pathTable, loggingContext);
            PipDataBuilderPool = new ObjectPool<PipDataBuilder>(() => new PipDataBuilder(StringTable), builder => builder.Clear());
        }

        /// <nodoc />
        public FrontEndContext(PipExecutionContext context, LoggingContext loggingContext, IFileSystem fileSystem, IFrontEndConfiguration frontEndConfig)
            : base(context)
        {
            Contract.Requires(loggingContext != null);
            Contract.Requires(fileSystem != null);

            LoggingContext = loggingContext;
            FileSystem = fileSystem;
            CredentialScanner = new CredentialScanner(frontEndConfig, context.PathTable, loggingContext);
            PipDataBuilderPool = new ObjectPool<PipDataBuilder>(() => new PipDataBuilder(StringTable), builder => builder.Clear());
        }

        /// <nodoc />
        [SuppressMessage("Microsoft.Design", "CA1011:Consider Changing type of 'pathTable' from PathTable to HierarchicalNameTable", Justification = "Completely and utterly bogus suggestion")]
        public static FrontEndContext CreateInstanceForTesting(PathTable pathTable = null, SymbolTable symbolTable = null, QualifierTable qualifierTable = null, IFileSystem fileSystem = null, CancellationToken? cancellationToken = null, IFrontEndConfiguration frontEndConfig = null)
        {
            pathTable = pathTable ?? new PathTable();
            return new FrontEndContext(
                pathTable,
                symbolTable ?? new SymbolTable(pathTable.StringTable),
                qualifierTable ?? new QualifierTable(pathTable.StringTable),
                new LoggingContext("UnitTest"),
                fileSystem ?? new PassThroughFileSystem(pathTable), // TODO: Consider moving this entire function into test helpers and then use the test file system.
                frontEndConfig ?? new FrontEndConfiguration(),
                cancellationToken ?? CancellationToken.None);
        }

#if DEBUG
        /// <summary>
        /// global instance for pretty printing some paths and strings while debugging.
        /// </summary>
        /// <summary>
        /// TODO: We still need to solve Debugging the pathTable/StringTable properly. This is on the backlog, so we have a temp workaround here.
        /// </summary>
        public static FrontEndContext DebugContext { get; private set; }

        /// <summary>
        /// Sets the singleton debugging context with the given context.
        /// </summary>
        public static void SetContextForDebugging(FrontEndContext frontEndContext)
        {
            Contract.Requires(frontEndContext != null);
            DebugContext = frontEndContext;
        }
#endif

        /// <summary>
        /// This is a temporary hook until we have filesystems that wrap other file systems to implement speccache and engine tracking.
        /// </summary>
        public void SetFileSystem(IFileSystem fileSystem)
        {
            FileSystem = fileSystem;
        }
    }
}
