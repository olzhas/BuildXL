// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using BuildXL.Scheduler.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;

#nullable enable

namespace BuildXL.Scheduler
{
    /// <summary>
    /// The context containing managing the lifetime of an operation
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public readonly struct OperationContext : IDisposable
    {
        /// <summary>
        /// Gets whether the operation context is valid
        /// </summary>
        public bool IsValid => LoggingContext != null;

        /// <summary>
        /// The current logging context
        /// </summary>
        public readonly LoggingContext LoggingContext;

        /// <summary>
        /// The current active operation for the context
        /// </summary>
        private readonly OperationTracker.Operation? m_operation;

        /// <summary>
        /// Gets the duration of the operation
        /// </summary>
        public TimeSpan? Duration => m_operation?.Duration;

        private OperationTracker.OperationThread? Thread
        {
            get
            {
                Contract.Assert(m_operation == null || m_operation.Thread != null);
                return m_operation?.Thread;
            }
        }

        /// <summary>
        /// Creates a new operation context
        /// </summary>
        internal OperationContext(LoggingContext loggingContext, OperationTracker.Operation? operation)
        {
            Contract.Requires(loggingContext != null);

            LoggingContext = loggingContext;
            m_operation = operation;
        }

        /// <summary>
        /// Starts a new operation (optionally as a new operation thread)
        /// </summary>
        /// <param name="kind">the operation kind</param>
        /// <param name="artifact">the associated file artifact if any</param>
        /// <param name="details">associated details about operation</param>
        public OperationContext StartOperation(OperationKind kind, in FileOrDirectoryArtifact artifact = default(FileOrDirectoryArtifact), string? details = null)
        {
            var operation = Thread?.StartNestedOperation(kind, artifact, details);
            return new OperationContext(LoggingContext, operation);
        }
        
        /// <summary>
        /// Reports timing for an externally tracked nested operation
        /// </summary>
        public void ReportExternalOperation(OperationKind kind, TimeSpan duration)
        {
            Thread?.ActiveOperation?.ReportExternalOperation(kind, duration);
        }

        /// <summary>
        /// Starts an operation on a new thread
        /// </summary>
        /// <param name="kind">the operation kind</param>
        /// <param name="artifact">the associated file artifact if any</param>
        /// <param name="details">associated details about operation</param>
        public OperationContext StartAsyncOperation(OperationKind kind, in FileOrDirectoryArtifact artifact = default(FileOrDirectoryArtifact), string? details = null)
        {
            var operation = Thread?.StartThread(kind, artifact, details);
            Contract.Assert(operation == null || operation.IsThread, "Async operations must start new thread");
            return new OperationContext(LoggingContext, operation);
        }

        /// <summary>
        /// Completes the operation
        /// </summary>
        public void Dispose()
        {
            m_operation?.Complete();
        }

        /// <summary>
        /// Implicitly converts operation context to its wrapped logging context
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates")]
        public static implicit operator LoggingContext(OperationContext operationContext)
        {
            return operationContext.LoggingContext;
        }

        /// <summary>
        /// Creates an operation context which does not have an associated tracked operation
        /// </summary>
        public static OperationContext CreateUntracked(LoggingContext loggingContext)
        {
            return new OperationContext(loggingContext, operation: null);
        }
    }
}
