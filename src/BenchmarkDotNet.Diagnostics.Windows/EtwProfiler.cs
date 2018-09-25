﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Diagnostics.Windows.Tracing;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using JetBrains.Annotations;
using Microsoft.Diagnostics.Tracing.Session;

namespace BenchmarkDotNet.Diagnostics.Windows
{
    public class EtwProfiler : IDiagnoser, IHardwareCountersDiagnoser, IProfiler
    {
        private readonly EtwProfilerConfig config;
        private readonly RunMode runMode;
        private readonly Dictionary<BenchmarkCase, string> benchmarkToEtlFile;
        private readonly Dictionary<BenchmarkCase, PreciseMachineCounter[]> benchmarkToCounters;

        private Session kernelSession, userSession;

        [PublicAPI] // parameterless ctor required by DiagnosersLoader to support creating this profiler via console line args
        public EtwProfiler() : this(new EtwProfilerConfig(performExtraBenchmarksRun: false)) { }
        
        [PublicAPI]
        public EtwProfiler(EtwProfilerConfig config)
        {
            this.config = config;
            runMode = config.PerformExtraBenchmarksRun ? RunMode.ExtraRun : RunMode.NoOverhead;
            benchmarkToEtlFile = new Dictionary<BenchmarkCase, string>();
            benchmarkToCounters = new Dictionary<BenchmarkCase, PreciseMachineCounter[]>();
        }

        public IEnumerable<string> Ids => new [] { nameof(EtwProfiler) };
        
        public IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();
        
        public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

        public IReadOnlyDictionary<BenchmarkCase, PmcStats> Results => ImmutableDictionary<BenchmarkCase, PmcStats>.Empty;
        
        public RunMode GetRunMode(BenchmarkCase benchmarkCase) => runMode;

        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
            => HardwareCounters.Validate(validationParameters, mandatory: false);

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            // it's crucial to start the trace before the process starts and stop it after the benchmarked process stops to have all of the necessary events in the trace file!
            if (signal == HostSignal.BeforeProcessStart)
                Start(parameters);
            else if (signal == HostSignal.AfterProcessExit)
                Stop(parameters);
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
        {
            if (!benchmarkToEtlFile.TryGetValue(results.BenchmarkCase, out var traceFilePath))
                return Array.Empty<Metric>();

            return TraceLogParser.Parse(traceFilePath, benchmarkToCounters[results.BenchmarkCase]);
        }

        public void DisplayResults(ILogger logger)
        {
            if (!benchmarkToEtlFile.Any())
                return;
            
            logger.WriteLineInfo($"Exported {benchmarkToEtlFile.Count} trace file(s). Example:");
            logger.WriteLineInfo(benchmarkToEtlFile.Values.First());
        }

        private void Start(DiagnoserActionParameters parameters)
        {
            var counters = benchmarkToCounters[parameters.BenchmarkCase] = parameters.Config
                .GetHardwareCounters()
                .Select(counter => HardwareCounters.FromCounter(counter, config.IntervalSelectors.TryGetValue(counter, out var selector) ? selector : GetInterval))
                .ToArray();

            if (counters.Any()) // we need to enable the counters before starting the kernel session
                HardwareCounters.Enable(counters);
            
            userSession = new UserSession(parameters, config).EnableProviders();
            kernelSession = new KernelSession(parameters, config).EnableProviders();
        }

        private void Stop(DiagnoserActionParameters parameters)
        {
            try
            {
                kernelSession.Stop();
                userSession.Stop();

                benchmarkToEtlFile[parameters.BenchmarkCase] = userSession.MergeFiles(kernelSession);
            }
            finally
            {
                kernelSession.Dispose();
                userSession.Dispose();
            }
        }

        private static int GetInterval(ProfileSourceInfo info) => Math.Min(info.MaxInterval, Math.Max(info.MinInterval, info.Interval));
    }
}