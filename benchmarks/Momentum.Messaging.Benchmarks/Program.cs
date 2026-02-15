using BenchmarkDotNet.Running;
using Momentum.Messaging;

[assembly: MomentumMediator]

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
