module Fugue.Tests.TestCollections

open System.Runtime.InteropServices
open Xunit

/// Tests that mutate process-wide environment variables must run sequentially to avoid
/// flaky failures caused by parallel test execution clobbering shared env-var state.
[<CollectionDefinition("Sequential", DisableParallelization = true)>]
type SequentialCollectionDef() = class end

/// FactAttribute that skips on Windows. Use for tests that rely on Unix shell
/// primitives (/bin/sh, chmod, cat, echo exit codes) that are not available
/// or behave differently under pwsh on Windows.
type FactUnlessWindowsAttribute() =
    inherit FactAttribute()
    do
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            base.Skip <- "Skipped on Windows: requires Unix shell (/bin/sh, chmod)"
