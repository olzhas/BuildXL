// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as Managed from "Sdk.Managed";
import * as Shared from "Sdk.Managed.Shared";
import * as SysMng from "System.Management";
import * as MacServices from "BuildXL.Sandbox.MacOS";
import * as GrpcSdk from "Sdk.Protocols.Grpc";

namespace Processes {

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Processes",
        sources: [
            ...globR(d`.`, "*.cs"),
            ...GrpcSdk.generateCSharp({
                    proto: [f`Remoting/Proto/Remote.proto`]
                }).sources
            ],
        generateLogs: true,
        references: [
            ...addIf(BuildXLSdk.isFullFramework,
                BuildXLSdk.NetFx.System.IO.Compression.dll,
                BuildXLSdk.NetFx.System.Management.dll,
                BuildXLSdk.NetFx.System.Net.Http.dll,
                NetFx.Netstandard.dll
            ),
            ...addIf(BuildXLSdk.isDotNetCoreOrStandard,
                SysMng.pkg.override<Shared.ManagedNugetPackage>({
                    runtime: [
                        Shared.Factory.createBinaryFromFiles(SysMng.Contents.all.getFile(r`runtimes/win/lib/netcoreapp2.0/System.Management.dll`))
                    ]
                }),
                importFrom("System.IO.Pipelines").pkg
            ),
            ...importFrom("BuildXL.Utilities").Native.securityDlls,
            importFrom("BuildXL.Pips").dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Interop.dll,
            importFrom("BuildXL.Utilities").Storage.dll,
            importFrom("BuildXL.Utilities").Collections.dll,
            importFrom("BuildXL.Utilities").Configuration.dll,
            importFrom("BuildXL.Utilities").Plugin.dll,
            importFrom("BuildXL.Utilities").PluginGrpc.dll,
            importFrom("Newtonsoft.Json").pkg,
            ...BuildXLSdk.systemThreadingTasksDataflowPackageReference,
            ...addIfLazy(BuildXLSdk.Flags.isMicrosoftInternal, () => [
                  importFrom("AnyBuild.SDK").pkg,
            ]),
            ...importFrom("BuildXL.Cache.ContentStore").getProtobufPackages(),
        ],
        internalsVisibleTo: [
            "Test.BuildXL.Engine",
            "Test.BuildXL.Processes",
            "Test.BuildXL.Processes.Detours",
            "Test.BuildXL.Scheduler",
            "ExternalToolTest.BuildXL.Scheduler",
        ],
        runtimeContent: [
            ...addIfLazy(Context.getCurrentHost().os === "win" && qualifier.targetRuntime === "win-x64", () => [
                importFrom("BuildXL.Sandbox.Windows").Deployment.detours,
            ]),
            ...addIfLazy(Context.getCurrentHost().os === "macOS" && qualifier.targetRuntime === "osx-x64", () => [
                MacServices.Deployment.bxlESDaemon,
            ]),
            ...addIfLazy(MacServices.Deployment.macBinaryUsage !== "none" && qualifier.targetRuntime === "osx-x64", () => [
                MacServices.Deployment.kext,
                MacServices.Deployment.sandboxMonitor,
                MacServices.Deployment.sandboxLoadScripts
            ]),
        ],
    });
}
