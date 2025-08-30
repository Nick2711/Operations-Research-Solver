using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;
using Solver.Engine.Simplex;
using CoreConstraint = Solver.Engine.Core.Constraint;

namespace Solver.Engine.Integer
{
    /// <summary>
    /// 0-1 Knapsack — Branch & Bound "Knapsack Method" with:
    /// - Sort by value/weight (desc), "Ratio Test" table
    /// - Greedy fractional upper bound with full TRACE lines (xk = 1 / a/b, capacity updates)
    /// - Branch on the FIRST FRACTIONAL item from the greedy fill
    /// - Sub-problem blocks labeled: Sub-P 1, Sub-P 2, Sub-P 2.1, Sub-P 2.2, ...
    /// - Candidate / Infeasible / Best Candidate annotations
    ///
    /// If the model is not a pure knapsack (MAX, 1 <= constraint, all BIN), this falls back
    /// to your existing solvers automatically.
    /// </summary>
    public sealed class Knapsack01Solver : ISolver
    {
        public string Name => "0-1 Knapsack (B&B, knapsack method)";

        public SolverResult Solve(LpModel model)
        {
            bool isPure = IsPureKnapsack(model, out int n, out CoreConstraint cap);
            bool hasAnyInt = model.Variables.Any(v => v.Sign is SignRestriction.Int or SignRestriction.Bin);

            if (!isPure)
            {
                ISolver fallback = hasAnyInt
                    ? new BranchAndBoundSimplexSolver()
                    : (model.Direction == OptimizeDirection.Min ? new DualSimplexSolver() : new PrimalSimplexSolver());
                return fallback.Solve(model);
            }

            return SolveKnapsack(model, n, cap);
        }

        // ────────────────────────────────────────────────────────────────────
        // Pure knapsack path
        // ────────────────────────────────────────────────────────────────────

        private sealed record Item(int Rank /*0..n-1 by ratio*/, int Index /*original*/,
                                   double Value, double Weight, double Ratio);

        private sealed class TraceResult
        {
            public double Bound;
            public int? FractionalRank;            // rank k* to branch on (null if all integer)
            public bool IsIntegerCandidate;        // true if greedy produced all 0/1
            public bool[] GreedyTakes = Array.Empty<bool>(); // in Rank order
            public List<string> Steps = new();     // lines: "x5 = 1 15-4=11", "x1 = 7/12 7-12"
            public double CandidateValue;          // = Bound when IsIntegerCandidate
        }

        private sealed class PathFixes
        {
            // In Rank order
            public bool[] Take;      // fixed 1's
            public bool[] Forbid;    // fixed 0's
            public PathFixes(int n)
            {
                Take = new bool[n];
                Forbid = new bool[n];
            }
            public PathFixes Clone()
            {
                var p = new PathFixes(Take.Length);
                Array.Copy(Take, p.Take, Take.Length);
                Array.Copy(Forbid, p.Forbid, Take.Length);
                return p;
            }
        }

        private SolverResult SolveKnapsack(LpModel model, int n, CoreConstraint capacity)
        {
            var log = new List<string>();
            double[] values = model.Variables.Select(v => v.ObjectiveCoeff).ToArray();
            double[] weights = capacity.Coeffs.ToArray();
            double C = capacity.Rhs;

            // Order by ratio (desc)
            var items = Enumerable.Range(0, n)
                .Select(i => new Item(0, i, values[i], weights[i],
                    weights[i] <= 0 ? double.PositiveInfinity : values[i] / weights[i]))
                .OrderByDescending(it => it.Ratio)
                .ThenBy(it => it.Index)
                .ToArray();

            for (int k = 0; k < n; k++)
                items[k] = items[k] with { Rank = k };

            // Ratio Test block
            var intro = new StringBuilder();
            intro.AppendLine("═══════════════════════════════════════════════════════════════════════");
            intro.AppendLine("Branch & Bound Algorithm — Knapsack method (fractional UB)");
            intro.AppendLine($"Capacity C = {Fmt(C)}    Items n = {n}");
            intro.AppendLine();
            intro.AppendLine("Ratio Test");
            intro.AppendLine("Item     v_i     w_i       v_i/w_i    Rank");
            for (int k = 0; k < n; k++)
            {
                var it = items[k];
                intro.AppendLine($"x{it.Index + 1,-3} {Fmt(it.Value),8} {Fmt(it.Weight),8} {Fmt(it.Ratio),12} {k + 1,5}");
            }
            intro.AppendLine("═══════════════════════════════════════════════════════════════════════");
            log.Add(intro.ToString());

            // Optional IP model block
            var mdl = new StringBuilder();
            mdl.AppendLine("Integer Programming Model");
            var z = string.Join(" + ", Enumerable.Range(0, n).Select(i => $"{TrimZero(values[i])}x{i + 1}"));
            var cstr = string.Join(" + ", Enumerable.Range(0, n).Select(i => $"{TrimZero(weights[i])}x{i + 1}"));
            mdl.AppendLine($"max z = {z}");
            mdl.AppendLine($"s.t. {cstr} ≤ {Fmt(C)}");
            mdl.AppendLine("x_i ∈ {0,1}");
            mdl.AppendLine();
            log.Add(mdl.ToString());

            // Global incumbent
            double bestVal = 0.0;
            bool[] bestX_byIndex = new bool[n];

            // Run a throwaway trace on the root to find first fractional item
            var rootFix = new PathFixes(n);
            var throwaway = new List<string>();
            ExploreSubProblem("Sub-P 1/2 setup", rootFix, items, C, values, weights,
                              throwaway, out _, traceOnly: true, allowRecursion: false,
                              ref bestVal, ref bestX_byIndex);
            int? rootFrac = LastTrace?.FractionalRank ?? 0;

            // Headline section
            log.Add("Branch & Bound — Sub-Problems");

            // Sub-P 1: set that item = 0
            var fixes1 = rootFix.Clone();
            fixes1.Forbid[rootFrac.Value] = true;
            ExploreSubProblem("Sub-P 1", fixes1, items, C, values, weights, log,
                              out var sub1Cand, traceOnly: false, allowRecursion: false,
                              ref bestVal, ref bestX_byIndex);
            if (sub1Cand.Value > bestVal)
            {
                bestVal = sub1Cand.Value;
                bestX_byIndex = ToXByIndex(sub1Cand.Takes, items, n);
            }

            // Sub-P 2: set that item = 1 (and allow deeper recursion)
            var fixes2 = rootFix.Clone();
            fixes2.Take[rootFrac.Value] = true;
            ExploreSubProblem("Sub-P 2", fixes2, items, C, values, weights, log,
                              out var sub2Cand, traceOnly: false, allowRecursion: true,
                              ref bestVal, ref bestX_byIndex);
            if (sub2Cand.Value > bestVal)
            {
                bestVal = sub2Cand.Value;
                bestX_byIndex = ToXByIndex(sub2Cand.Takes, items, n);
            }

            // Final solution print
            var sol = new StringBuilder();
            sol.AppendLine("Best Candidate");
            sol.AppendLine($"z = {Fmt(bestVal)}");
            for (int i = 0; i < n; i++) sol.AppendLine($"x{i + 1} = {(bestX_byIndex[i] ? 1 : 0)}");
            log.Add(sol.ToString());

            var x = bestX_byIndex.Select(b => b ? 1.0 : 0.0).ToArray();
            return new SolverResult(true, bestVal, x, log);
        }

        // We cache the last bound trace so we can know the first fractional item at the root
        private TraceResult? LastTrace;

        /// <summary>
        /// Explore one sub-problem block:
        /// - prints "*" for fixed decisions and shows capacity updates
        /// - runs greedy UB with trace lines
        /// - if integer candidate → prints Candidate
        /// - else branches on the FIRST FRACTIONAL item from the trace:
        ///      child ".1" sets it = 0 ; child ".2" sets it = 1
        /// Updates incumbent (bestVal / bestX_byIndex) via ref.
        /// </summary>
        private void ExploreSubProblem(
            string label,
            PathFixes fixes,
            Item[] items, double C,
            double[] values, double[] weights,
            IList<string> logBlock,
            out (double Value, bool[] Takes) candidate,
            bool traceOnly,
            bool allowRecursion,
            ref double bestVal,
            ref bool[] bestX_byIndex)
        {
            var sb = new StringBuilder();
            sb.AppendLine(label);
            double rem0 = C;

            // Show starred/fixed decisions like slides
            for (int k = 0; k < items.Length; k++)
            {
                var it = items[k];
                if (fixes.Take[k])
                {
                    double before = rem0;
                    rem0 -= it.Weight;
                    sb.AppendLine($"* x{it.Index + 1} = 1 {FmtInt(before)}-{FmtInt(it.Weight)}={FmtInt(rem0)}");
                }
                if (fixes.Forbid[k])
                {
                    double before = rem0;
                    sb.AppendLine($"* x{it.Index + 1} = 0 {FmtInt(before)}-0={FmtInt(rem0)}");
                }
            }

            // Infeasible immediately?
            double fixedW = 0.0, fixedV = 0.0;
            for (int k = 0; k < items.Length; k++)
            {
                if (fixes.Take[k]) { fixedW += items[k].Weight; fixedV += items[k].Value; }
            }
            if (fixedW > C + 1e-9)
            {
                sb.AppendLine("Infeasible");
                logBlock.Add(sb.ToString());
                candidate = (0.0, new bool[items.Length]);
                LastTrace = null;
                return;
            }

            // Greedy UB with trace
            var tr = FractionalBoundWithTrace(items, C, fixes);
            LastTrace = tr;

            foreach (var line in tr.Steps) sb.AppendLine(line);

            if (tr.IsIntegerCandidate)
            {
                sb.AppendLine($"z = {Fmt(tr.CandidateValue)} ");
                sb.AppendLine("Candidate");
                logBlock.Add(sb.ToString());
                candidate = (tr.CandidateValue, tr.GreedyTakes.ToArray());
                return;
            }

            // Not integer → show split
            candidate = (0.0, tr.GreedyTakes);
            if (traceOnly)
            {
                logBlock.Add(sb.ToString());
                return;
            }

            int kStar = tr.FractionalRank!.Value;

            // First child: fractional item = 0
            var f1 = fixes.Clone();
            f1.Forbid[kStar] = true;
            ExploreSubProblem(label + ".1", f1, items, C, values, weights, logBlock,
                              out var cand1, traceOnly: false, allowRecursion: allowRecursion,
                              ref bestVal, ref bestX_byIndex);
            if (cand1.Value > bestVal)
            {
                bestVal = cand1.Value;
                bestX_byIndex = ToXByIndex(cand1.Takes, items, values.Length);
            }

            // Second child: fractional item = 1
            var f2 = fixes.Clone();
            f2.Take[kStar] = true;
            ExploreSubProblem(label + ".2", f2, items, C, values, weights, logBlock,
                              out var cand2, traceOnly: false, allowRecursion: allowRecursion,
                              ref bestVal, ref bestX_byIndex);
            if (cand2.Value > bestVal)
            {
                bestVal = cand2.Value;
                bestX_byIndex = ToXByIndex(cand2.Takes, items, values.Length);
            }

            // Append this block at the end (after either candidate or branching)
            // (We already appended the lines above; keeping for symmetry.)
            // logBlock.Add(sb.ToString()); // optional
        }

        private TraceResult FractionalBoundWithTrace(Item[] items, double C, PathFixes fixes)
        {
            var tr = new TraceResult
            {
                GreedyTakes = new bool[items.Length]
            };

            double w = 0.0, z = 0.0;

            // start from fixed takes
            for (int k = 0; k < items.Length; k++)
            {
                if (fixes.Take[k])
                {
                    w += items[k].Weight;
                    z += items[k].Value;
                    tr.GreedyTakes[k] = true;
                }
            }

            double rem = C - w;

            // Greedy over remaining (skip fixed/forbidden)
            for (int k = 0; k < items.Length; k++)
            {
                if (fixes.Take[k] || fixes.Forbid[k]) continue;
                if (rem <= 1e-12) break;

                var it = items[k];
                if (it.Weight <= rem + 1e-12)
                {
                    // take fully
                    double before = rem;
                    z += it.Value;
                    rem -= it.Weight;
                    w += it.Weight;
                    tr.GreedyTakes[k] = true;
                    tr.Steps.Add($"x{it.Index + 1} = 1 {FmtInt(before)}-{FmtInt(it.Weight)}={FmtInt(rem)}");
                }
                else
                {
                    // fractional
                    double before = rem;
                    double frac = rem / it.Weight;
                    z += it.Value * frac;
                    tr.FractionalRank = k;
                    tr.Steps.Add($"x{it.Index + 1} = {FmtFrac(rem, it.Weight)} {FmtInt(before)}-{FmtInt(it.Weight)}");
                    rem = 0.0;
                    break;
                }
            }

            tr.Bound = z;
            tr.IsIntegerCandidate = !tr.FractionalRank.HasValue;
            if (tr.IsIntegerCandidate) tr.CandidateValue = z;
            return tr;
        }

        private static bool IsPureKnapsack(LpModel model, out int n, out CoreConstraint capacity)
        {
            n = model.NumVars;
            capacity = model.Constraints.Count > 0 ? model.Constraints[0] : new CoreConstraint(Array.Empty<double>(), Relation.LessOrEqual, 0);

            if (model.Direction != OptimizeDirection.Max) return false;
            if (model.NumConstraints != 1) return false;

            var con = model.Constraints[0];
            if (con.Relation != Relation.LessOrEqual) return false;
            if (model.Variables.Any(v => v.Sign != SignRestriction.Bin)) return false;

            capacity = con;
            return true;
        }

        // ────────────────────────────────────────────────────────────────────
        // Utilities / formatting
        // ────────────────────────────────────────────────────────────────────

        private static string Fmt(double x) => x.ToString("0.####", CultureInfo.InvariantCulture);

        private static string TrimZero(double x)
        {
            var s = x.ToString("0.####", CultureInfo.InvariantCulture);
            if (s == "-0") s = "0";
            return s;
        }

        // integer-looking format for capacities/weights in the trace
        private static string FmtInt(double x)
        {
            double r = Math.Round(x);
            return Math.Abs(x - r) < 1e-9 ? ((long)r).ToString(CultureInfo.InvariantCulture) : Fmt(x);
        }

        private static string FmtFrac(double num, double den)
        {
            // If both are (near) integers, print as a/b; else decimal
            double nRound = Math.Round(num), dRound = Math.Round(den);
            if (Math.Abs(num - nRound) < 1e-9 && Math.Abs(den - dRound) < 1e-9 && dRound != 0.0)
            {
                long a = (long)nRound, b = (long)dRound;
                long g = Gcd(Math.Abs(a), Math.Abs(b));
                return $"{a / g}/{b / g}";
            }
            return Fmt(num / den);
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = a % b; a = b; b = t; }
            return a == 0 ? 1 : Math.Abs(a);
        }

        private static bool[] ToXByIndex(bool[] takesByRank, Item[] items, int n)
        {
            var x = new bool[n];
            for (int k = 0; k < items.Length; k++)
                if (takesByRank[k]) x[items[k].Index] = true;
            return x;
        }
    }
}
