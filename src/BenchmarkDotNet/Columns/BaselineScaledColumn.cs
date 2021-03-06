﻿using System;
using System.Linq;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace BenchmarkDotNet.Columns
{
    public class BaselineScaledColumn : IColumn
    {
        public enum DiffKind
        {
            Mean,
            StdDev,
            WelchTTestPValue
        }

        public static readonly IColumn Scaled = new BaselineScaledColumn(DiffKind.Mean);
        public static readonly IColumn ScaledStdDev = new BaselineScaledColumn(DiffKind.StdDev);

        public static IColumn CreateWelchTTest(Hypothesis h) => new BaselineScaledColumn(DiffKind.WelchTTestPValue, h);

        public DiffKind Kind { get; }
        private readonly Hypothesis hypothesis; 

        private BaselineScaledColumn(DiffKind kind, Hypothesis hypothesis = null)
        {
            Kind = kind;
            this.hypothesis = hypothesis;
        }

        public string Id => nameof(BaselineScaledColumn) + "." + Kind;

        public string ColumnName
        {
            get
            {
                switch (Kind)
                {
                    case DiffKind.Mean:
                        return "Scaled";
                    case DiffKind.StdDev:
                        return "ScaledSD";
                    case DiffKind.WelchTTestPValue:
                        return "p-value";
                    default:
                        throw new NotSupportedException();
                }
            }
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            string logicalGroupKey = summary.GetLogicalGroupKey(benchmarkCase);
            var baseline = summary.GetBaseline(logicalGroupKey);
            bool isBaseline = summary.IsBaseline(benchmarkCase);
            bool invalidResults = baseline == null ||
                                 summary[baseline] == null ||
                                 summary[baseline].ResultStatistics == null ||
                                 !summary[baseline].ResultStatistics.CanBeInverted() ||
                                 summary[benchmarkCase] == null ||
                                 summary[benchmarkCase].ResultStatistics == null;

            if (invalidResults)
                return "?";

            var baselineStat = summary[baseline].ResultStatistics;
            var targetStat = summary[benchmarkCase].ResultStatistics;

            double mean = isBaseline ? 1 : Statistics.DivMean(targetStat, baselineStat);
            double stdDev = isBaseline ? 0 : Math.Sqrt(Statistics.DivVariance(targetStat, baselineStat));

            switch (Kind)
            {
                case DiffKind.Mean:
                    return IsNonBaselinesPrecise(summary, baselineStat, benchmarkCase) ? mean.ToStr("N3") : mean.ToStr("N2");
                case DiffKind.StdDev:
                    return stdDev.ToStr("N2");
                case DiffKind.WelchTTestPValue:
                {
                    if (baselineStat.N < 2 || targetStat.N < 2)
                        return "NA";
                    double pvalue = WelchTTest.Calc(baselineStat, targetStat).PValue;
                    return pvalue > 0.0001 || pvalue < 1e-9 ? pvalue.ToStr("N4") : pvalue.ToStr("e2");
                }
                default:
                    throw new NotSupportedException();
            }
        }

        private static bool IsNonBaselinesPrecise(Summary summary, Statistics baselineStat, BenchmarkCase benchmarkCase)
        {
            string logicalGroupKey = summary.GetLogicalGroupKey(benchmarkCase);
            var nonBaselines = summary.GetNonBaselines(logicalGroupKey);

            return nonBaselines.Any(x => Statistics.DivMean(summary[x].ResultStatistics, baselineStat) < 0.01);
        }

        public bool IsAvailable(Summary summary) => summary.HasBaselines();
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Baseline;
        public int PriorityInCategory => (int) Kind;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, ISummaryStyle style) => GetValue(summary, benchmarkCase);
        public override string ToString() => ColumnName;
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public string Legend
        {
            get
            {
                switch (Kind)
                {
                    case DiffKind.Mean:
                        return "Mean(CurrentBenchmark) / Mean(BaselineBenchmark)";
                    case DiffKind.StdDev:
                        return "Standard deviation of ratio of distribution of [CurrentBenchmark] and [BaselineBenchmark]";
                    case DiffKind.WelchTTestPValue:
                        return $"p-value for Welch's t-test against baseline (H1: {hypothesis.H1})";
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Kind));
                }
            }
        }
    }
}
