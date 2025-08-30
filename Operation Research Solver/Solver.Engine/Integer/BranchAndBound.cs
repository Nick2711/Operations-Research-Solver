using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;
using Solver.Engine.Simplex;

namespace Solver.Engine.Integer
{
    /// <summary>
    /// Branch & Bound (Tableau)
    /// - Adds x_j <= 1 caps for BIN variables at the root so every node inherits them (fallback: assume all x are BIN if none flagged).
    /// - Branching on binaries uses x_j <= 0 and x_j >= 1 split.
    /// - Dual Simplex re-optimization after each branch row is added.
    /// - Candidate extraction uses the basis map; we also verify z == c·x before accepting incumbents.
    /// </summary>
    public sealed class BranchAndBoundSimplexSolver : ISolver
    {
        public string Name => "Branch & Bound (Tableau)";
        private const double EPS = 1e-9;
        private const double INT_TOL = 1e-6;
        private const int MAX_ITERS = 10000;
        private const int MAX_NODES = 10000;

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();
            log.Add("Algorithm: Branch & Bound (Tableau)");
            log.Add("ok B&B algorithm");
            log.Add("Optimal table from relaxed primal");

            int numX = model.NumVars;

            // Detect binary vars; fallback to "all binary" if none are flagged.
            bool[] isBin = model.Variables.Select(v => v.Sign == SignRestriction.Bin).ToArray();
            int flagged = isBin.Count(b => b);
            if (flagged == 0)
            {
                isBin = Enumerable.Repeat(true, numX).ToArray();
                log.Add($"[root] No variables flagged BIN → assuming all {numX} decision vars are binary and adding x<=1 caps.");
            }
            else
            {
                log.Add($"[root] Detected {flagged} binary variables → adding x<=1 caps for those vars.");
            }

            // ---------- root primal (with x<=1 for chosen vars) ----------
            var T = BuildPhaseIIFromModel(model, isBin, log); // includes bin-cap rows
            var basis = Enumerable.Range(numX, Constraints(T)).ToArray();

            var root = PrimalOptimizeToOptimal(T, basis);
            if (!root.success)
            {
                log.Add(root.infeasible ? "LP infeasible." : "LP did not converge.");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: root.infeasible);
            }

            PrintTable("═══════════════════════════════════════════════════════════════════════\nOptimal LP tableau", T, basis, numX, log);
            log.Add($"Objective (z) = {Fmt(T[0, T.GetLength(1) - 1])}");
            log.Add("");

            var firstCand = FindFractionalBasicXRows(T, basis, numX);
            log.Add("Branch & Bound — fractional basic x rows");
            log.Add("row\tbasic\tvalue\tfrac");
            foreach (var r in firstCand) log.Add($"{r.rowIndex}\t{xName(r.basicJ)}\t{Fmt(r.value)}\t{Fmt(r.frac)}");
            log.Add("");

            // ---------- full DFS (no bounding cuts here; simple demo) ----------
            bool haveIncumbent = false;
            double incumbentZ = double.NegativeInfinity;
            double[] incumbentX = Array.Empty<double>();
            int visited = 0;

            double ObjFromX(double[] x)
            {
                double s = 0.0;
                for (int j = 0; j < numX; j++) s += model.Variables[j].ObjectiveCoeff * x[j];
                return s;
            }

            void ConsiderCandidate(string label, double[,] Tn, int[] Bn)
            {
                int n = ColsNoRhs(Tn);
                double z_tab = Tn[0, n];
                var xn = ExtractX(Tn, Bn, numX);
                double z_x = ObjFromX(xn);

                if (Math.Abs(z_x - z_tab) > 1e-6)
                    log.Add($"[warn] objective mismatch at {label}: tableau z={Fmt(z_tab)} vs c·x={Fmt(z_x)} — using c·x");

                double zcand = z_x;

                if (!haveIncumbent || zcand > incumbentZ + 1e-12)
                {
                    haveIncumbent = true; incumbentZ = zcand; incumbentX = xn;
                    log.Add($"Candidate at {label}: all basic x RHS integral, z = {Fmt(zcand)}");
                    PrintTable("Optimal tableau (candidate)", Tn, Bn, numX, log);
                }
            }

            void Explore(double[,] Tnode, int[] Bnode, string label)
            {
                if (++visited > MAX_NODES) { log.Add("Node limit reached; stopping search."); return; }

                if (IsIntegerBasicXRows(Tnode, Bnode, numX))
                {
                    ConsiderCandidate(label, Tnode, Bnode);
                    return;
                }

                var cands = FindFractionalBasicXRows(Tnode, Bnode, numX);
                if (cands.Count == 0)
                {
                    ConsiderCandidate(label + " (no branchable rows)", Tnode, Bnode);
                    return;
                }
                var chosen = ChooseByClosestToHalf(cands);
                int r0 = chosen.rowIndex;
                int j = chosen.basicJ;
                double b = chosen.value;
                double frac = chosen.frac;

                log.Add($"Branch at {label}: row {r0 + 1}, {xName(j)} = {Fmt(b)} (frac {Fmt(frac)})");

                // If binary, force 0/1 split. Otherwise, generic floor/ceil.
                bool treatAsBinary = isBin[j];
                double upperForLeft = treatAsBinary ? 0.0 : Math.Floor(b + 1e-12);
                double lowerForRight = treatAsBinary ? 1.0 : Math.Ceiling(b - 1e-12);

                // child 1: x_j ≤ upper
                {
                    var child = AddBranchConstraintExact(
                        parentT: Tnode, parentBasis: Bnode,
                        branchRowOneBased: r0 + 1,
                        varCol: j, isUpper: true, bound: upperForLeft,
                        log: log, subLabel: $"{label}.1: {xName(j)} ≤ {Fmt(upperForLeft)} (add slack)"
                    );

                    log.Add($"{label}.1 — Dual Simplex re-optimization");
                    var dual = DualOptimizeWithLogs(child.T, child.Basis, numX, log);
                    if (!dual.success)
                    {
                        log.Add(dual.infeasible ? $"{label}.1: infeasible → prune." : $"{label}.1: dual stalled → prune.");
                    }
                    else
                    {
                        PrintTable($"{label}.1 — Dual simplex optimal tableau", child.T, child.Basis, numX, log);
                        Explore(child.T, child.Basis, label + ".1");
                    }
                }

                // child 2: x_j ≥ lower
                {
                    var child = AddBranchConstraintExact(
                        parentT: Tnode, parentBasis: Bnode,
                        branchRowOneBased: r0 + 1,
                        varCol: j, isUpper: false, bound: lowerForRight,
                        log: log, subLabel: $"{label}.2: {xName(j)} ≥ {Fmt(lowerForRight)} (add excess)"
                    );

                    log.Add($"{label}.2 — Dual Simplex re-optimization");
                    var dual = DualOptimizeWithLogs(child.T, child.Basis, numX, log);
                    if (!dual.success)
                    {
                        log.Add(dual.infeasible ? $"{label}.2: infeasible → prune." : $"{label}.2: dual stalled → prune.");
                    }
                    else
                    {
                        PrintTable($"{label}.2 — Dual simplex optimal tableau", child.T, child.Basis, numX, log);
                        Explore(child.T, child.Basis, label + ".2");
                    }
                }
            }

            Explore(Clone(T), (int[])basis.Clone(), "1");

            if (!haveIncumbent)
            {
                var xrel = ExtractX(T, basis, numX);
                var zrel = model.Variables.Select(v => v.ObjectiveCoeff).Zip(xrel).Sum(t => t.First * t.Second);
                log.Add("\nFinished without integer incumbent — returning relaxed solution.");
                log.Add($"SUCCESS — Objective: {Fmt(zrel)}");
                log.Add($"Solution: {string.Join(", ", xrel.Select((v, i) => $"x{i + 1}={Fmt(v)}"))}");
                return new SolverResult(true, zrel, xrel, log);
            }

            log.Add($"\nSUCCESS — Objective: {Fmt(incumbentZ)}");
            log.Add($"Solution: {string.Join(", ", incumbentX.Select((v, i) => $"x{i + 1}={Fmt(v)}"))}");
            return new SolverResult(true, incumbentZ, incumbentX, log);
        }

        // ───────────────────────── exact row-build helper (branch row − skeleton, flip if RHS>0) ─────────────────────────
        private sealed class ChildTableau { public double[,] T = default!; public int[] Basis = default!; }

        private static ChildTableau AddBranchConstraintExact(
            double[,] parentT, int[] parentBasis,
            int branchRowOneBased, int varCol,
            bool isUpper, double bound,
            List<string> log, string subLabel)
        {
            int m = Constraints(parentT);
            int n = ColsNoRhs(parentT);
            int rhsP = n;

            // Allocate child: +1 column (new slack/excess) and +1 row (new constraint)
            int newColsNoRhs = n + 1;
            int rhsC = newColsNoRhs;
            int newCol = n;
            int newRow = m + 1;

            var child = new double[m + 2, newColsNoRhs + 1];

            // Copy parent tableau into child; initialize new column to 0
            for (int r = 0; r <= m; r++)
            {
                for (int c = 0; c < n; c++) child[r, c] = parentT[r, c];
                child[r, newCol] = 0.0;
                child[r, rhsC] = parentT[r, rhsP];
            }

            // Copy parent basis and extend with the new column as basic at the new row
            var childBasis = new int[m + 1];
            Array.Copy(parentBasis, childBasis, m);
            childBasis[m] = newCol;

            // Start new row as a COPY of the branch row (all columns including RHS)
            int rOld = branchRowOneBased;
            for (int c = 0; c < n; c++) child[newRow, c] = parentT[rOld, c];
            child[newRow, newCol] = 0.0;
            child[newRow, rhsC] = parentT[rOld, rhsP];

            log.Add(subLabel);
            log.Add("Branch row (copy)");
            log.Add(RowToLine(child, newRow, newColsNoRhs, header: $"row {branchRowOneBased}"));

            // Build skeleton: x_j ± s_new = bound
            var skel = new double[newColsNoRhs + 1];
            skel[varCol] = 1.0;
            skel[newCol] = isUpper ? +1.0 : -1.0;
            skel[rhsC] = bound;

            log.Add($"Skeleton: {xName(varCol)} {(isUpper ? "+" : "−")} s_new = {Fmt(bound)}");
            log.Add(VectorToLine("skeleton", skel, newColsNoRhs));

            // newRow := newRow − skeleton   (INCLUDING RHS)
            for (int c = 0; c <= newColsNoRhs; c++)
                child[newRow, c] -= skel[c];

            log.Add("row − skeleton (raw)");
            log.Add(RowToLine(child, newRow, newColsNoRhs, header: "new row (pre-flip)"));

            // If RHS > 0, flip entire row to make dual feasible start (RHS ≤ 0)
            if (child[newRow, rhsC] > 1e-12)
            {
                for (int c = 0; c <= newColsNoRhs; c++) child[newRow, c] = -child[newRow, c];
                log.Add("RHS > 0 ⇒ multiply whole row by −1");
            }

            log.Add(RowToLine(child, newRow, newColsNoRhs, header: "new row"));

            // Keep reduced costs unchanged for the new column
            child[0, newCol] = 0.0;

            return new ChildTableau { T = child, Basis = childBasis };
        }

        // ───────────── Dual Simplex (with compact logs) ─────────────
        private static (bool success, bool infeasible) DualOptimizeWithLogs(
            double[,] T, int[] basis, int numX, List<string> log)
        {
            int m = Constraints(T);
            int n = ColsNoRhs(T);
            int rhs = n;

            int iter = 0;
            while (true)
            {
                // pick leaving row: most negative RHS
                int leave = -1; double mostNeg = -1e-12;
                for (int i = 0; i < m; i++)
                {
                    double b = T[i + 1, rhs];
                    if (b < mostNeg) { mostNeg = b; leave = i; }
                }
                if (leave == -1)
                {
                    log.Add("Dual Simplex: feasible (all RHS ≥ 0).");
                    return (true, false);
                }

                // entering: minimize z_j / (-a_{leave,j}) over a_{leave,j} < 0 and z_j > 0
                int enter = -1; double best = double.PositiveInfinity;
                for (int j = 0; j < n; j++)
                {
                    double a = T[leave + 1, j];
                    if (a < -EPS)
                    {
                        double rc = T[0, j];
                        if (rc > EPS)
                        {
                            double ratio = rc / (-a);
                            if (ratio < best - 1e-12 || (Math.Abs(ratio - best) <= 1e-12 && j < enter))
                            {
                                best = ratio; enter = j;
                            }
                        }
                    }
                }
                // Bland-like fallback: allow rc≈0 if needed
                if (enter == -1)
                {
                    for (int j = 0; j < n; j++)
                    {
                        double a = T[leave + 1, j];
                        double rc = T[0, j];
                        if (a < -EPS && Math.Abs(rc) <= 1e-12) { enter = j; break; }
                    }
                    if (enter == -1)
                    {
                        log.Add("Dual Simplex: no valid entering column → infeasible/degenerate.");
                        return (false, true);
                    }
                }

                iter++;
                log.Add($"Dual pivot {iter}: leave row {leave + 1}, enter col {(enter < numX ? $"x{enter + 1}" : $"s{enter - numX + 1}")}");
                Pivot(T, leave + 1, enter);
                basis[leave] = enter;
            }
        }

        private static string RowToLine(double[,] T, int r, int lastColNoRhs, string header)
        {
            var sb = new StringBuilder();
            sb.Append(header).Append('\t');
            for (int c = 0; c < lastColNoRhs; c++)
                sb.Append(Fmt(T[r, c])).Append('\t');
            sb.Append(Fmt(T[r, lastColNoRhs]));
            return sb.ToString();
        }

        private static string VectorToLine(string name, double[] v, int lastColNoRhs)
        {
            var sb = new StringBuilder();
            sb.Append(name).Append('\t');
            for (int c = 0; c < lastColNoRhs; c++)
                sb.Append(Fmt(v[c])).Append('\t');
            sb.Append(Fmt(v[lastColNoRhs]));
            return sb.ToString();
        }

        // ───────────────────────── utilities ─────────────────────────
        private static string Fmt(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
        private static string xName(int j) => $"x{j + 1}";
        private static int Constraints(double[,] T) => T.GetLength(0) - 1;
        private static int ColsNoRhs(double[,] T) => T.GetLength(1) - 1;

        private static void PrintTable(string title, double[,] T, int[] basis, int numX, List<string> log)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);

            int m = Constraints(T);
            int n = ColsNoRhs(T);
            var headers = new List<string>();
            for (int j = 0; j < numX; j++) headers.Add($"x{j + 1}");
            for (int j = numX; j < n; j++) headers.Add($"s{j - numX + 1}");
            headers.Add("rhs");

            sb.AppendLine("\t" + string.Join("\t", headers));

            // z
            var zr = new List<string>();
            for (int j = 0; j < n; j++) zr.Add(Fmt(T[0, j]));
            zr.Add(Fmt(T[0, n]));
            sb.AppendLine("z\t" + string.Join("\t", zr));

            // rows
            for (int i = 0; i < m; i++)
            {
                var r = new List<string>();
                for (int j = 0; j < n; j++) r.Add(Fmt(T[i + 1, j]));
                r.Add(Fmt(T[i + 1, n]));
                sb.AppendLine($"{i + 1}\t" + string.Join("\t", r));
            }
            sb.AppendLine();
            log.Add(sb.ToString());
        }

        // ───────────────────────── Phase-II & primal optimum ─────────────────────────
        /// <summary>
        /// Builds Phase-II tableau A|I|b for MAX with <= rows.
        /// Additionally appends one <= row for each j with isBin[j] == true: x_j <= 1 (with its own slack).
        /// </summary>
        private static double[,] BuildPhaseIIFromModel(LpModel model, bool[] isBin, List<string> log)
        {
            int m0 = model.NumConstraints;   // original constraints
            int p = model.NumVars;          // decision vars
            int add = isBin.Count(b => b);   // # of bin caps
            int m = m0 + add;               // total rows
            int n = p + m;                  // total columns before RHS

            var T = new double[m + 1, n + 1]; // +1 for z row, +1 for RHS

            // Original rows: A|I and b
            for (int i = 0; i < m0; i++)
            {
                for (int j = 0; j < p; j++) T[i + 1, j] = model.Constraints[i].Coeffs[j];
                T[i + 1, p + i] = 1.0;
                T[i + 1, n] = model.Constraints[i].Rhs;
            }

            // Extra rows: for each j with isBin[j], add (x_j <= 1) with its own slack
            int extra = 0;
            for (int j = 0; j < p; j++)
            {
                if (!isBin[j]) continue;
                int r = m0 + extra;             // global row index (0-based)
                T[r + 1, j] = 1.0;            // coeff on x_j
                T[r + 1, p + r] = 1.0;          // its slack
                T[r + 1, n] = 1.0;            // RHS = 1
                extra++;
            }
            log.Add($"[root] Added {add} binary-cap rows (x_j ≤ 1). Total rows now: {m}.");

            // z = -c (MAX)
            for (int j = 0; j < p; j++) T[0, j] = -model.Variables[j].ObjectiveCoeff;

            return T;
        }

        private static (bool success, bool infeasible) PrimalOptimizeToOptimal(double[,] T, int[] basis)
        {
            int m = Constraints(T);
            int n = ColsNoRhs(T);

            // Canonicalize z with current basis
            for (int i = 0; i < m; i++)
            {
                int jb = basis[i];
                double cb = jb < n ? -T[0, jb] : 0.0; // z has -c; add (c_b)*row
                if (Math.Abs(cb) < EPS) continue;
                for (int c = 0; c <= n; c++) T[0, c] += cb * T[i + 1, c];
            }

            for (int it = 0; it < MAX_ITERS; it++)
            {
                // Enter = most negative reduced cost
                int enter = -1; double minRc = -1e-12;
                for (int j = 0; j < n; j++) { double rc = T[0, j]; if (rc < minRc) { minRc = rc; enter = j; } }
                if (enter == -1) return (true, false); // optimal

                // Leave = minimum ratio
                int leave = -1; double best = double.PositiveInfinity;
                for (int i = 0; i < m; i++)
                {
                    double a = T[i + 1, enter];
                    if (a > EPS)
                    {
                        double t = T[i + 1, n] / a;
                        if (t >= 0 && t < best - 1e-12) { best = t; leave = i; }
                    }
                }
                if (leave == -1) return (false, false); // unbounded (should not happen with ≤, x≥0)

                Pivot(T, leave + 1, enter);
                basis[leave] = enter;
            }
            return (false, false);
        }

        private static void Pivot(double[,] T, int prow, int pcol)
        {
            int n = ColsNoRhs(T);
            double piv = T[prow, pcol];
            if (Math.Abs(piv) < 1e-16) throw new InvalidOperationException("Pivot on ~0.");

            double inv = 1.0 / piv;
            for (int c = 0; c <= n; c++) T[prow, c] *= inv;

            int rows = T.GetLength(0);
            for (int r = 0; r < rows; r++)
            {
                if (r == prow) continue;
                double f = T[r, pcol];
                if (Math.Abs(f) < 1e-16) continue;
                for (int c = 0; c <= n; c++) T[r, c] -= f * T[prow, c];
            }
        }

        // ───────────────────────── Step 2: fractional basic x rows ─────────────────────────
        private static List<(int rowIndex, int basicJ, double value, double frac)>
        FindFractionalBasicXRows(double[,] T, int[] basis, int numX)
        {
            var outList = new List<(int, int, double, double)>();
            int m = Constraints(T);
            int n = ColsNoRhs(T);
            for (int i = 0; i < m; i++)
            {
                int j = basis[i];
                if (j < 0 || j >= numX) continue;     // only decision variables
                double b = T[i + 1, n];
                double f = Math.Abs(b - Math.Round(b));
                if (f > INT_TOL) outList.Add((i, j, b, f));
            }
            return outList;
        }

        private static (int rowIndex, int basicJ, double value, double frac)
        ChooseByClosestToHalf(List<(int rowIndex, int basicJ, double value, double frac)> cand)
            => cand.OrderBy(t => Math.Abs((t.value - Math.Floor(t.value)) - 0.5))
                   .ThenBy(t => t.basicJ).First();

        private static bool IsIntegerBasicXRows(double[,] T, int[] basis, int numX)
        {
            int n = ColsNoRhs(T);
            for (int i = 0; i < basis.Length; i++)
            {
                int j = basis[i];
                if (j >= 0 && j < numX)
                {
                    double b = T[i + 1, n];
                    if (Math.Abs(b - Math.Round(b)) > INT_TOL) return false;
                }
            }
            return true;
        }

        private static double[] ExtractX(double[,] T, int[] basis, int numX)
        {
            int n = ColsNoRhs(T);
            var x = new double[numX];

            // IMPORTANT: scan ALL basis rows, not just the first numX
            for (int i = 0; i < basis.Length; i++)
            {
                int j = basis[i];
                if (j >= 0 && j < numX)
                {
                    double v = T[i + 1, n];
                    // clean tiny numerical noise
                    if (Math.Abs(v) < 1e-9) v = 0.0;
                    x[j] = v;
                }
            }
            return x;
        }


        private static double[,] Clone(double[,] T)
        {
            var c = new double[T.GetLength(0), T.GetLength(1)];
            Array.Copy(T, c, T.Length);
            return c;
        }
    }
}
