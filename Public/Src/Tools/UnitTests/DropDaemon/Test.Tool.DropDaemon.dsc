// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import {Cmd} from "Sdk.Transformers";

namespace Test.Tool.DropDaemon {
    export const dll = !(BuildXLSdk.Flags.isMicrosoftInternal && Context.getCurrentHost().os === "win") ? undefined : BuildXLSdk.test({
        assemblyName: "Test.Tool.DropDaemon",
        sources: globR(d`.`, "*.cs"),
        appConfig: f`Test.Tool.DropDaemon.dll.config`,
        assemblyBindingRedirects: importFrom("BuildXL.Tools.DropDaemon").dropDaemonBindingRedirects(),
        references: [
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
            importFrom("BuildXL.Cache.ContentStore").UtilitiesCore.dll,
            importFrom("BuildXL.Engine").Scheduler.dll,
            importFrom("BuildXL.Tools.DropDaemon").exe,
            importFrom("BuildXL.Tools").ServicePipDaemon.dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Ipc.dll,
            importFrom("BuildXL.Utilities").Ipc.Providers.dll,
            importFrom("BuildXL.Utilities").Storage.dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities.Instrumentation").Tracing.dll,
            importFrom("BuildXL.Utilities.Instrumentation").Common.dll,
            importFrom("ArtifactServices.App.Shared").pkg,
            importFrom("ItemStore.Shared").pkg,
            importFrom("Drop.App.Core").pkg,
            importFrom("Drop.Client").pkg,
            importFrom("Microsoft.AspNet.WebApi.Client").pkg,
            importFrom("Microsoft.VisualStudio.Services.ArtifactServices.Shared").pkg,
            importFrom("Microsoft.VisualStudio.Services.BlobStore.Client").pkg,
            importFrom("Microsoft.VisualStudio.Services.BlobStore.Client.Cache").pkg,
            importFrom("Microsoft.IdentityModel.Clients.ActiveDirectory").pkg,
            importFrom("Microsoft.VisualStudio.Services.Client").pkg,
            importFrom("Microsoft.VisualStudio.Services.InteractiveClient").pkg,
            importFrom("Microsoft.Azure.Storage.Common").pkg,

            // SBOM related
            ...importFrom("BuildXL.Tools.DropDaemon").dropDaemonSbomPackages(),
        ],

        runtimeContentToSkip: importFrom("BuildXL.Tools.DropDaemon").dropDaemonRuntimeContentToSkip(),
    });
}
