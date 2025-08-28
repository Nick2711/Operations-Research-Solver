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
    /// 0-1 Knapsack with Branch & Bound (fractional upper bound) and neat tabular output.
    /// If the model is not a pure knapsack (MAX + single "<=" capacity + all bin),
    /// this solver automatically defers to your existing stack:
    ///   - BranchAndBoundSimplexSolver (if any int/bin vars)
    ///   - DualSimplexSolver for MIN
    ///   - PrimalSimplexSolver for MAX continuous
    /// </summary>
    public sealed class Knapsack01Solver : ISolver
    {
        public string Name => "0-1 Knapsack (B&B, pretty output)";

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

        // ---------- pure knapsack path ----------

        private sealed record Item(int Index, double Value, double Weight, double Ratio);

        private sealed class Node
        {
            public int Id { get; }
            public int? ParentId { get; }
            public string Path { get; }
            public int Level { get; }
            public double Profit { get; }
            public double Weight { get; }
            public BitSet Picks { get; }
            public BitSet Excluded { get; }
            public double Bound { get; set; }
            public string Note { get; set; } = "";

            public Node(int id, int? parentId, string path, int level, double profit, double weight,
                        BitSet picks, BitSet excluded)
            {
                Id = id; ParentId = parentId; Path = path;
                Level = level; Profit = profit; Weight = weight;
                Picks = picks; Excluded = excluded;
            }
        }

        private sealed class BitSet
        {
            private readonly ulong[] _w;
            public int Length { get; }
            public BitSet(int length) { Length = length; _w = new ulong[(length + 63) >> 6]; }
            private BitSet(int length, ulong[] words) { Length = length; _w = words; }
            public bool this[int i] => ((_w[i >> 6] >> (i & 63)) & 1UL) != 0UL;
            public BitSet Set(int i)
            {
                var w = (ulong[])_w.Clone();
                w[i >> 6] |= (1UL << (i & 63));
                return new BitSet(Length, w);
            }
        }

        private SolverResult SolveKnapsack(LpModel model, int n, CoreConstraint capacity)
        {
            var log = new List<string>();

            var values = model.Variables.Select(v => v.ObjectiveCoeff).ToArray();
            var weights = capacity.Coeffs.ToArray();
            var C = capacity.Rhs;

            var items = Enumerable.Range(0, n)
                .Select(i => new Item(
                    Index: i,
                    Value: values[i],
                    Weight: weights[i],
                    Ratio: (weights[i] <= 0 ? double.PositiveInfinity : values[i] / weights[i])
                ))
                .OrderByDescending(it => it.Ratio)
                .ToArray();

            int[] posOf = new int[n];
            for (int rank = 0; rank < n; rank++) posOf[items[rank].Index] = rank;

            // Header similar to your sheets, but tighter and aligned
            var hdr = new StringBuilder();
            hdr.AppendLine("═══════════════════════════════════════════════════════════════════════");
            hdr.AppendLine("  Branch & Bound — Knapsack Method (Fractional UB)");
            hdr.AppendLine($"  Items: n={n}    Capacity: C={Fmt(C)}");
            hdr.AppendLine("  Order by value/weight (desc):");
            for (int k = 0; k < n; k++)
                hdr.AppendLine($"    k={k:00}  →  x{items[k].Index + 1}   v={Fmt(items[k].Value),6}   w={Fmt(items[k].Weight),6}   r={Fmt(items[k].Ratio),8}");
            hdr.AppendLine("═══════════════════════════════════════════════════════════════════════");
            log.Add(hdr.ToString());

            int nextId = 1;
            var root = new Node(id: 0, parentId: null, path: "root", level: 0,
                                profit: 0, weight: 0,
                                picks: new BitSet(n), excluded: new BitSet(n));
            root.Bound = FractionalBound(root, items, C);

            var stack = new Stack<Node>();
            stack.Push(root);

            double zStar = 0.0;
            var best = new BitSet(n);

            var table = new StringBuilder();
            table.AppendLine(TableHeader());
            int visited = 0;

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                visited++;

                if (node.Weight > C + 1e-12)
                {
                    table.AppendLine(TableRow(node, C, "PRUNE", "capacity"));
                    continue;
                }
                if (node.Bound <= zStar + 1e-9)
                {
                    table.AppendLine(TableRow(node, C, "PRUNE", "bound"));
                    continue;
                }

                if (node.Level == n)
                {
                    if (node.Profit > zStar + 1e-9)
                    {
                        zStar = node.Profit;
                        best = node.Picks;
                        table.AppendLine(TableRow(node, C, "LEAF*", "incumbent"));
                    }
                    else
                    {
                        table.AppendLine(TableRow(node, C, "LEAF", ""));
                    }
                    continue;
                }

                int k = node.Level;
                var it = items[k];

                // Exclude (right)
                var ex = new Node(id: nextId++, parentId: node.Id, path: node.Path + ".R", level: k + 1,
                                  profit: node.Profit, weight: node.Weight,
                                  picks: node.Picks, excluded: node.Excluded.Set(k));
                ex.Bound = FractionalBound(ex, items, C);
                ex.Note = $"x{it.Index + 1}=0";
                table.AppendLine(TableRow(ex, C, "→", "exclude"));

                // Include (left)
                var inW = node.Weight + it.Weight;
                var inP = node.Profit + it.Value;
                var inc = new Node(id: nextId++, parentId: node.Id, path: node.Path + ".L", level: k + 1,
                                   profit: inP, weight: inW,
                                   picks: node.Picks.Set(k), excluded: node.Excluded);
                inc.Bound = FractionalBound(inc, items, C);
                inc.Note = $"x{it.Index + 1}=1";
                table.AppendLine(TableRow(inc, C, "→", "include"));

                // DFS: include first helps tighten z* earlier
                stack.Push(ex);
                stack.Push(inc);
            }

            table.AppendLine("───────────────────────────────────────────────────────────────────────");
            table.AppendLine($"Visited nodes: {visited}");
            log.Add(table.ToString());

            var x = new double[n];
            for (int k = 0; k < n; k++)
                if (best[k]) x[items[k].Index] = 1.0;

            var totalW = Dot(weights, x);
            var totalV = Dot(values, x);

            var sol = new StringBuilder();
            sol.AppendLine("=== Solution ===");
            for (int i = 0; i < n; i++) sol.AppendLine($"x{i + 1} = {(x[i] > 0.5 ? 1 : 0)}");
            sol.AppendLine($"Total value (objective) = {Fmt(totalV)}");
            sol.AppendLine($"Total weight = {Fmt(totalW)} (capacity {Fmt(C)})");
            log.Add(sol.ToString());

            return new SolverResult(true, totalV, x, log);
        }

        // ---------- helpers ----------

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

        private static string TableHeader()
        {
            return
                "Node  Parent  Path         Lvl  Next  Decision   Profit      Weight    RemCap     Bound    Status (why)\n" +
                "----- ------- ------------ ---- ----- ---------- ---------- ---------- ---------- ---------- ----------------";
        }

        private static string TableRow(Node n, double capacity, string status, string why)
        {
            string next = n.Level.ToString(CultureInfo.InvariantCulture).PadLeft(2);
            string parent = n.ParentId?.ToString() ?? "-";
            string dec = string.IsNullOrEmpty(n.Note) ? "-" : n.Note;
            string rem = Fmt(Math.Max(0.0, capacity - n.Weight));

            return string.Format(CultureInfo.InvariantCulture,
                "{0,-5} {1,-7} {2,-12} {3,4}  {4,2}    {5,-10} {6,10} {7,10} {8,10} {9,10} {10} {11}",
                n.Id,
                parent,
                (n.Path.Length > 12 ? n.Path[^12..] : n.Path),
                n.Level,
                next,
                dec,
                Fmt(n.Profit),
                Fmt(n.Weight),
                rem,
                Fmt(n.Bound),
                status,
                (string.IsNullOrEmpty(why) ? "" : $"({why})")
            );
        }

        private static double FractionalBound(Node node, Item[] items, double C)
        {
            double profit = node.Profit;
            double weight = node.Weight;

            for (int k = node.Level; k < items.Length; k++)
            {
                if (node.Excluded[k]) continue;
                var it = items[k];
                if (weight >= C) break;

                if (weight + it.Weight <= C)
                {
                    weight += it.Weight;
                    profit += it.Value;
                }
                else
                {
                    double remain = C - weight;
                    if (remain > 1e-12 && it.Weight > 0)
                    {
                        profit += it.Value * (remain / it.Weight);
                        weight = C;
                    }
                    break;
                }
            }
            return profit;
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0.0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        private static string Fmt(double x) => x.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
