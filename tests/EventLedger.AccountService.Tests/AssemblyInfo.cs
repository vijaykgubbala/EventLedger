using Xunit;

// See EventLedger.Gateway.Tests/AssemblyInfo.cs for why: Console.Out
// redirection and Serilog's static Log.Logger are process-wide global
// state that races under xUnit's default parallel-by-class execution.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
