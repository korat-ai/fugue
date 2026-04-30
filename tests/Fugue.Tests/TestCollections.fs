module Fugue.Tests.TestCollections

open Xunit

/// Tests that mutate process-wide environment variables must run sequentially to avoid
/// flaky failures caused by parallel test execution clobbering shared env-var state.
[<CollectionDefinition("Sequential", DisableParallelization = true)>]
type SequentialCollectionDef() = class end
