// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;

namespace Test.BuildXL.Scheduler
{
    public static class SchedulerTestHelper
    {
        /// <summary>
        /// Helper to create a PipBuilder and optionally assign a tag
        /// </summary>
        internal static ProcessBuilder CreatePipBuilderWithTag(TestEnv env, string tag = null)
        {
            var exe = FileArtifact.CreateSourceFile(AbsolutePath.Create(env.Context.PathTable, @"\\dummyPath\DummyFile.exe"));

            var processBuilder = ProcessBuilder.Create(env.PathTable, env.PipDataBuilderPool.GetInstance(), env.FrontEndContext.CredentialScanner, env.FrontEndContext.LoggingContext);
            processBuilder.Executable = exe;
            processBuilder.AddInputFile(exe);
            processBuilder.AddTags(env.PathTable.StringTable, tag);

            return processBuilder;
        }
        
        /// <summary>
        /// Builds the <see cref="TestEnv.PipGraph"/> and asserts success
        /// </summary>
        internal static PipGraph AssertSuccessGraphBuilding(TestEnv env)
        {
            var builder = env.PipGraph as PipGraph.Builder;

            XAssert.IsNotNull(builder);
            var pipGraph = builder.Build();
            XAssert.IsNotNull(pipGraph);
            return pipGraph;
        }

        /// <summary>
        /// Builds the <see cref="TestEnv.PipGraph"/> and asserts failure
        /// </summary>
        internal static void AssertFailedGraphBuilding(TestEnv env)
        {
            var builder = env.PipGraph as PipGraph.Builder;

            XAssert.IsNotNull(builder);
            XAssert.IsNull(builder.Build());
        }
    }
}
