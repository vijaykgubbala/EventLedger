using Xunit;

// Tests in this assembly share process-wide global state (Console.Out
// redirection in logging tests, Serilog's static Log.Logger reassigned by
// every WebApplicationFactory<Program> construction). xUnit's default
// parallel-by-class execution races these against each other. Disable
// parallelization for this assembly rather than each test protecting
// itself individually.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
