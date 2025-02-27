// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using BuildXL.Ipc.Common;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Engine
{
    /// <summary>
    /// A pip graph that fails when someone tries to add pips.
    /// </summary>
    /// <remarks>
    /// This graph is intended to be used when processing Config and Module build files.
    /// </remarks>
    internal sealed class DisallowedGraph : IMutablePipGraph, IPipScheduleTraversal
    {
        private LoggingContext m_loggingContext;

        public DisallowedGraph(LoggingContext loggingContext)
        {
            m_loggingContext = loggingContext;
        }

        /// <inheritdoc />
        public bool AddProcess(Process process, PipId valuePip)
        {
            Contract.Requires(process != null, "Argument process cannot be null");
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        public bool AddIpcPip(IpcPip ipcPip, PipId valuePip)
        {
            Contract.Requires(ipcPip != null, "Argument pip cannot be null");
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        public bool AddCopyFile(CopyFile copyFile, PipId valuePip)
        {
            Contract.Requires(copyFile != null, "Argument copyFile cannot be null");
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        public bool AddWriteFile(WriteFile writeFile, PipId valuePip)
        {
            Contract.Requires(writeFile != null, "Argument writeFile cannot be null");
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        public DirectoryArtifact AddSealDirectory(SealDirectory sealDirectory, PipId valuePip)
        {
            Contract.Requires(sealDirectory != null);
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return DirectoryArtifact.Invalid;
        }

        /// <inheritdoc />
        public bool AddOutputValue(ValuePip value)
        {
            Contract.Requires(value != null, "Argument outputValue cannot be null");

            // Value pips are ok to generate, Value pips are just not allowed to create executable pips.
            return true;
        }

        /// <inheritdoc />
        public bool AddValueValueDependency(in ValuePip.ValueDependency valueDependency)
        {
            Contract.Requires(valueDependency.ParentIdentifier.IsValid);
            Contract.Requires(valueDependency.ChildIdentifier.IsValid);

            // Value to value pip dependencies are also allowed
            return true;
        }

        /// <inheritdoc />
        public bool AddSpecFile(SpecFilePip specFile)
        {
            Contract.Requires(specFile != null, "Argument specFile cannot be null");
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        [SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords")]
        public bool AddModule(ModulePip module)
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        public bool AddModuleModuleDependency(ModuleId moduleId, ModuleId dependency)
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return false;
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public IEnumerable<Pip> RetrieveScheduledPips()
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            yield break;
        }

        /// <inheritdoc />
        public IEnumerable<Pip> RetrievePipImmediateDependencies(Pip pip)
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            yield break;
        }

        /// <inheritdoc />
        public IEnumerable<Pip> RetrievePipImmediateDependents(Pip pip)
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            yield break;
        }

        /// <inheritdoc />
        public int PipCount => 0;

        /// <summary>
        /// Creates a new moniker if it hasn't already been created; otherwise returns the previously created one.
        /// </summary>
        public IpcMoniker GetApiServerMoniker()
        {
            return default;
        }

        /// <inheritdoc />
        public GraphPatchingStatistics PartiallyReloadGraph(HashSet<AbsolutePath> affectedSpecs)
        {
            Contract.Requires(affectedSpecs != null);
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return default(GraphPatchingStatistics);
        }

        /// <inheritdoc />
        public void SetSpecsToIgnore(IEnumerable<AbsolutePath> specsToIgnore)
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
        }

        /// <inheritdoc />
        public DirectoryArtifact ReserveSharedOpaqueDirectory(AbsolutePath directoryArtifactRoot)
        {
            return DirectoryArtifact.CreateWithZeroPartialSealId(directoryArtifactRoot);
        }

        /// <inheritdoc />
        public bool ApplyCurrentOsDefaults(ProcessBuilder processBuilder)
        {
            return false;
        }

        /// <inheritdoc />
        public bool TryGetSealDirectoryKind(DirectoryArtifact directoryArtifact, out SealDirectoryKind kind)
        {
            kind = default(SealDirectoryKind);
            return false;
        }

        /// <inheritdoc />
        public Pip GetPipFromPipId(PipId pipId) => null;
        
        /// <inheritdoc/>
        public bool TryAssertOutputExistenceInOpaqueDirectory(DirectoryArtifact outputDirectoryArtifact, AbsolutePath outputInOpaque, out FileArtifact fileArtifact)
        {
            fileArtifact = FileArtifact.Invalid;
            return false;
        }

        /// <inheritdoc />
        public IReadOnlyCollection<KeyValuePair<DirectoryArtifact, HashSet<FileArtifact>>> RetrieveOutputsUnderOpaqueExistenceAssertions()
        {
            Tracing.Logger.Log.CannotAddCreatePipsDuringConfigOrModuleEvaluation(m_loggingContext);
            return null;
        }
    }
}
