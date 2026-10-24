using Xunit;

// The UI language is process-wide state and the OS here is French, so every test that
// asserts on formatted text has to own the culture while it runs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
