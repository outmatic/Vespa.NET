using BenchmarkDotNet.Running;

// Run: dotnet run -c Release -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
