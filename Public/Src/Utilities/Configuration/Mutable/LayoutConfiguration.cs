// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class LayoutConfiguration : ILayoutConfiguration
    {
        /// <nodoc />
        public LayoutConfiguration()
        {
        }

        /// <nodoc />
        public LayoutConfiguration(ILayoutConfiguration template, PathRemapper pathRemapper)
        {
            Contract.Assume(template != null);
            Contract.Assume(pathRemapper != null);

            PrimaryConfigFile = pathRemapper.Remap(template.PrimaryConfigFile);
            SourceDirectory = pathRemapper.Remap(template.SourceDirectory);
            OutputDirectory = pathRemapper.Remap(template.OutputDirectory);
            ObjectDirectory = pathRemapper.Remap(template.ObjectDirectory);
            RedirectedDirectory = pathRemapper.Remap(template.RedirectedDirectory);
            FrontEndDirectory = pathRemapper.Remap(template.FrontEndDirectory);
            CacheDirectory = pathRemapper.Remap(template.CacheDirectory);
            EngineCacheDirectory = pathRemapper.Remap(template.EngineCacheDirectory);
            TempDirectory = pathRemapper.Remap(template.TempDirectory);
            BuildEngineDirectory = pathRemapper.Remap(template.BuildEngineDirectory);
            NormalizedBuildEngineDirectory = pathRemapper.Remap(template.NormalizedBuildEngineDirectory);
            FileContentTableFile = pathRemapper.Remap(template.FileContentTableFile);
            SchedulerFileChangeTrackerFile = pathRemapper.Remap(template.SchedulerFileChangeTrackerFile);
            IncrementalSchedulingStateFile = pathRemapper.Remap(template.IncrementalSchedulingStateFile);
            FingerprintStoreDirectory = pathRemapper.Remap(template.FingerprintStoreDirectory);
            SharedOpaqueSidebandDirectory = pathRemapper.Remap(template.SharedOpaqueSidebandDirectory);
            EmitSpotlightIndexingWarning = template.EmitSpotlightIndexingWarning;
            RedirectedUserProfileJunctionRoot = pathRemapper.Remap(template.RedirectedUserProfileJunctionRoot);
            ExternalSandboxedProcessDirectory = pathRemapper.Remap(template.ExternalSandboxedProcessDirectory);
        }

        /// <inheritdoc />
        public AbsolutePath PrimaryConfigFile { get; set; }

        /// <inheritdoc />
        public AbsolutePath SourceDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath OutputDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath ObjectDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath RedirectedDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath FrontEndDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath CacheDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath EngineCacheDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath TempDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath BuildEngineDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath NormalizedBuildEngineDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath FileContentTableFile { get; set; }

        /// <inheritdoc />
        public AbsolutePath SchedulerFileChangeTrackerFile { get; set; }

        /// <inheritdoc />
        public AbsolutePath IncrementalSchedulingStateFile { get; set; }

        /// <inheritdoc />
        public AbsolutePath FingerprintStoreDirectory { get; set; }

        /// <inheritdoc />
        public AbsolutePath SharedOpaqueSidebandDirectory { get; set; }

        /// <inheritdoc />
        public bool EmitSpotlightIndexingWarning { get; set; } = true;

        /// <inheritdoc />
        public AbsolutePath RedirectedUserProfileJunctionRoot { get; set; }

        /// <inheritdoc />
        public AbsolutePath ExternalSandboxedProcessDirectory { get; set; }
    }
}
