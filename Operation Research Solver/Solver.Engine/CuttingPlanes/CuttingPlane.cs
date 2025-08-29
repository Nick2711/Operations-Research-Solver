using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Solver.Engine.Core;
using Solver.Engine.Integer;
using Solver.Engine.Simplex;

namespace Solver.Engine.CuttingPlanes
{
    /// <summary>
    /// Gomory fractional cutting plane solver with spreadsheet-style logs.
    /// Native mode: MAX, INT/BIN vars (x ≥ 0), all constraints ≤ with RHS ≥ 0.
    /// If the model is not in that shape, we fall back to Branch & Bound Simplex.
    /// </summary>
    public sealed class CuttingPlaneSolver : ISolver
    {
        public string Name => "Cutting Planes (Gomory, Pretty)";

        private const double Eps = 1e-9;
        private const double IntTol = 1e-6;
        private const int MaxSimplexIter = 10_000;
        private const int MaxCuts = 200;

        public SolverResult Solve(LpModel model)
        {
            if (!IsPureIntegerNonnegLE(model, out string whyNot))
            {
                var bb = new BranchAndBoundSimplexSolver();
                var log = new List<string>
                {
                    "Cutting Plane: model is not pure IP with (≤, x≥0).",
                    "Reason: " + (whyNot.Length == 0 ? "N/A" : whyNot),
                    $"Falling back to: {bb.Name}"
                };
                var res = bb.Solve(model);
                if (res.Log is not null) log.AddRange(res.Log);
                return new SolverResult(res.Success, res.ObjectiveValue, res.X, log, res.Unbounded, res.Infeasible);
            }

            int n = model.NumVars;
            var c = model.Variables.Select(v => v.ObjectiveCoeff).ToArray(); // MAX

            // Build A,b from model constraints
            var A = new List<double[]>();
            var b = new List<double>();
            foreach (var con in model.Constraints) { A.Add((double[])con.Coeffs.Clone()); b.Add(con.Rhs); }

            // Add x≤1 for BIN variables so 0-1 is respected in relaxation
            int binAdded = 0;
            for (int j = 0; j < n; j++)
            {
                if (model.Variables[j].Sign == SignRestriction.Bin)
                {
                    var row = new double[n];
                    row[j] = 1.0;
                    A.Add(row);
                    b.Add(1.0);
                    binAdded++;
                }
            }

            var logCP = new List<string>
            {
                "═══════════════════════════════════════════════════════════════════════",
                "  Cutting Plane Algorithm — Gomory (fractional)",
                $"  Vars: n={n}   Constraints: m={A.Count}   (x ≥ 0; all INT/BIN)",
                (binAdded > 0 ? $"  Note: added {binAdded} binary bounds x≤1 to LP relaxation" : null),
                "═══════════════════════════════════════════════════════════════════════"
            }.Where(s => s != null).ToList()!;

            var tab = Tableau.Build(A, b, c);
            int tableNo = 1;
            var snap = new StringBuilder();
            Tableau.PrintTable(ref tab, n, "Canonical Form", "(z)", snap, showTheta: false);
            logCP.Add(snap.ToString()); snap.Clear();

            if (!Tableau.PrimalSimplexSnapshots(ref tab, n, ref tableNo, logCP, MaxSimplexIter))
            {
                var bb = new BranchAndBoundSimplexSolver();
                logCP.Add("LP relaxation failed → fallback.");
                var res = bb.Solve(model);
                if (res.Log is not null) logCP.AddRange(res.Log);
                return new SolverResult(res.Success, res.ObjectiveValue, res.X, logCP, res.Unbounded, res.Infeasible);
            }

            int cuts = 0;
            while (true)
            {
                var x = Tableau.ReadPrimalSolution(tab, n);
                double z = tab.Z;

                // Are all decision variables integral?
                bool allInt = true;
                for (int i = 0; i < n; i++)
                {
                    double v = x[i];
                    double d = Math.Min(v - Math.Floor(v), Math.Ceiling(v) - v);
                    if (d > IntTol) { allInt = false; break; }
                }
                if (allInt)
                {
                    logCP.Add("───────────────────────────────────────────────────────────────────────");
                    logCP.Add("All integer variables integral — stop.");
                    logCP.Add($"Objective (z) = {FmtFrac(z)}");
                    for (int i = 0; i < n; i++) logCP.Add($"x{i + 1} = {FmtFrac(x[i])}");
                    return new SolverResult(true, z, x, logCP);
                }

                // Choose Gomory row with your rules
                string reason;
                int row = Tableau.ChooseGomoryRowSmart(tab, n, out reason);
                if (row < 0)
                {
                    var rounded = Array.ConvertAll(x, v => Math.Round(v));
                    double approxZ = 0; for (int i = 0; i < n; i++) approxZ += c[i] * rounded[i];
                    logCP.Add("No fractional basic row to cut; returning rounded feasible.");
                    return new SolverResult(true, approxZ, rounded, logCP);
                }

                var (fracAll, f0) = Tableau.BuildGomoryCut(tab, row);
                int basicCol = tab.Basis[row];
                string cutOn = basicCol < n ? $"x{basicCol + 1}" : $"s{basicCol - n + 1}";

                logCP.Add("");
                logCP.Add($"Cutting Plane: Cut {cuts + 1}");
                logCP.Add($"Cut {cutOn} ({reason})");

                // FULL derivation (no ellipses)
                logCP.AddRange(Tableau.DeriveCutStepsFull(tab, row, n, cutOn));

                // Add cut (≤ form across all columns) and print T-k*
                Tableau.AddGomoryCut(ref tab, (fracAll, f0));
                Tableau.PrintTable(ref tab, n, $"T-{tableNo}*", "z", snap, showTheta: false);
                logCP.Add(snap.ToString()); snap.Clear();
                tableNo++;

                // Restore feasibility with Dual Simplex (robust entering rule)
                if (!Tableau.DualSimplexSnapshots(ref tab, n, ref tableNo, logCP, MaxSimplexIter))
                {
                    var bb = new BranchAndBoundSimplexSolver();
                    logCP.Add("Dual simplex could not proceed — falling back to Branch & Bound with current best.");
                    var res = bb.Solve(model);
                    if (res.Log is not null) logCP.AddRange(res.Log);
                    return new SolverResult(res.Success, res.ObjectiveValue, res.X, logCP, res.Unbounded, res.Infeasible);
                }

                cuts++;
                if (cuts >= MaxCuts)
                {
                    logCP.Add($"Reached cut limit ({MaxCuts}).");
                    var bestX = Tableau.ReadPrimalSolution(tab, n);
                    return new SolverResult(true, tab.Z, bestX, logCP);
                }
            }
        }

        // ───────────────────────── helpers & checks ─────────────────────────

        private static bool IsPureIntegerNonnegLE(LpModel m, out string whyNot)
        {
            var sb = new StringBuilder();
            if (m.Direction != OptimizeDirection.Max) sb.Append("Not MAX; ");
            if (m.Variables.Any(v => v.Sign != SignRestriction.Int && v.Sign != SignRestriction.Bin)) sb.Append("Vars not all INT/BIN; ");
            if (m.Variables.Any(v => v.Sign == SignRestriction.Minus || v.Sign == SignRestriction.Urs)) sb.Append("Some vars negative/free; ");
            if (m.Constraints.Any(c => c.Relation != Relation.LessOrEqual)) sb.Append("Non-≤ constraint exists; ");
            if (m.Constraints.Any(c => c.Rhs < -Eps)) sb.Append("Negative RHS; ");
            whyNot = sb.ToString();
            return whyNot.Length == 0;
        }

        private static string FmtFrac(double x) => Fraction.FormatMixed(x);

        // ───────────────────────── internal tableau ─────────────────────────

        private struct Tableau
        {
            public double[,] A;  // (m+1) x (n+slack+1); last column = RHS, last row = z
            public int M, N, Slack;
            public int[] Basis;
            public double Z;

            public static Tableau Build(List<double[]> Arows, List<double> b, double[] c)
            {
                int m = Arows.Count, n = c.Length, cols = n + m + 1;
                var T = new double[m + 1, cols];

                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++) T[i, j] = Arows[i][j];
                    T[i, n + i] = 1.0;                 // slack
                    T[i, cols - 1] = b[i];             // RHS
                }
                for (int j = 0; j < n; j++) T[m, j] = -c[j]; // MAX: z-row = -c
                T[m, cols - 1] = 0.0;

                var bas = new int[m];
                for (int i = 0; i < m; i++) bas[i] = n + i;

                return new Tableau { A = T, M = m, N = n, Slack = m, Basis = bas, Z = 0.0 };
            }

            public static void PrintTable(ref Tableau T, int n, string title, string zLabel, StringBuilder sb, bool showTheta, int? enterCol = null)
            {
                int cols = T.N + T.Slack + 1;
                var head = new List<string>();
                for (int j = 0; j < T.N; j++) head.Add($"x{j + 1}");
                for (int j = 0; j < T.Slack; j++) head.Add($"s{j + 1}");
                head.Add("rhs");

                sb.AppendLine(title);
                sb.AppendLine("\t" + string.Join("\t", head));

                // z-row
                var rowZ = new List<string>();
                for (int j = 0; j < cols - 1; j++) rowZ.Add(FmtFrac(T.A[T.M, j]));
                rowZ.Add(FmtFrac(T.A[T.M, cols - 1]));
                sb.AppendLine($"{zLabel}\t" + string.Join("\t", rowZ));

                for (int i = 0; i < T.M; i++)
                {
                    var row = new List<string>();
                    for (int j = 0; j < cols - 1; j++) row.Add(FmtFrac(T.A[i, j]));
                    row.Add(FmtFrac(T.A[i, cols - 1]));

                    string theta = "";
                    if (showTheta && enterCol.HasValue)
                    {
                        double aij = T.A[i, enterCol.Value];
                        if (aij > 1e-12) theta = FmtFrac(T.A[i, cols - 1] / aij);
                    }
                    sb.AppendLine($"{i + 1}\t" + string.Join("\t", row) + (showTheta ? "\t" + theta : ""));
                }
                sb.AppendLine();
            }

            public static bool PrimalSimplexSnapshots(ref Tableau T, int n, ref int tableNo, List<string> log, int maxIter)
            {
                int cols = T.N + T.Slack + 1, iter = 0;

                while (true)
                {
                    if (++iter > maxIter) { log.Add("Primal simplex: iteration limit."); return false; }

                    // MAX with z-row = -c: enter the MOST NEGATIVE reduced cost
                    int enter = -1; double mostNeg = 0.0;
                    for (int j = 0; j < cols - 1; j++)
                    {
                        double rc = T.A[T.M, j];
                        if (rc < mostNeg - 1e-12) { mostNeg = rc; enter = j; }
                    }
                    if (enter == -1)
                    {
                        T.Z = T.A[T.M, cols - 1];
                        var sb = new StringBuilder();
                        PrintTable(ref T, n, $"T-{tableNo}*", "z", sb, showTheta: false);
                        log.Add(sb.ToString()); tableNo++;
                        return true; // optimal (all rc >= 0)
                    }

                    var snap = new StringBuilder();
                    PrintTable(ref T, n, (tableNo == 1 ? "T-i" : $"T-{tableNo}"), "z", snap, showTheta: true, enterCol: enter);
                    log.Add(snap.ToString()); tableNo++;

                    // Leaving by min-ratio
                    int leave = -1; double minRatio = double.PositiveInfinity;
                    for (int i = 0; i < T.M; i++)
                    {
                        double aij = T.A[i, enter];
                        if (aij > 1e-12)
                        {
                            double ratio = T.A[i, cols - 1] / aij;
                            if (ratio >= -1e-12 && ratio < minRatio - 1e-12) { minRatio = ratio; leave = i; }
                        }
                    }
                    if (leave == -1)
                    {
                        log.Add("Unbounded in primal simplex.");
                        return false;
                    }

                    Pivot(ref T, leave, enter);
                }
            }

            public static bool DualSimplexSnapshots(ref Tableau T, int n, ref int tableNo, List<string> log, int maxIter)
            {
                int cols = T.N + T.Slack + 1, iter = 0;

                while (true)
                {
                    if (++iter > maxIter) { log.Add("Dual simplex: iteration limit."); return false; }

                    // Pick leaving row with most negative RHS
                    int leave = -1; double mostNeg = 0.0;
                    for (int i = 0; i < T.M; i++)
                    {
                        double rhs = T.A[i, cols - 1];
                        if (rhs < mostNeg - 1e-12) { mostNeg = rhs; leave = i; }
                    }
                    if (leave == -1)
                    {
                        T.Z = T.A[T.M, cols - 1];
                        var sb = new StringBuilder();
                        PrintTable(ref T, n, $"T-{tableNo}*", "z", sb, showTheta: false);
                        log.Add(sb.ToString()); tableNo++;
                        return true; // primal feasible
                    }

                    // Candidates: a_il < 0 with rc_j > 0; minimize rc_j / (-a_il)
                    int enter = -1; double best = double.PositiveInfinity;
                    for (int j = 0; j < cols - 1; j++)
                    {
                        double a = T.A[leave, j];
                        if (a < -1e-12)
                        {
                            double rc = T.A[T.M, j];
                            if (rc > 1e-12)
                            {
                                double ratio = rc / (-a);
                                if (ratio < best - 1e-12 || (Math.Abs(ratio - best) <= 1e-12 && j < enter))
                                {
                                    best = ratio; enter = j;
                                }
                            }
                        }
                    }

                    // Bland-like fallback if all rc_j ≤ 0 but we still have a_il < 0
                    if (enter == -1)
                    {
                        for (int j = 0; j < cols - 1; j++)
                        {
                            double a = T.A[leave, j];
                            double rc = T.A[T.M, j];
                            if (a < -1e-12 && Math.Abs(rc) <= 1e-12)
                            {
                                enter = j;
                                break;
                            }
                        }
                        if (enter == -1)
                        {
                            log.Add("Dual simplex: no valid entering column.");
                            return false;
                        }
                    }

                    var snap = new StringBuilder();
                    PrintTable(ref T, n, $"T-{tableNo}", "z", snap, showTheta: true, enterCol: enter);
                    log.Add(snap.ToString()); tableNo++;

                    Pivot(ref T, leave, enter);
                }
            }

            public static void Pivot(ref Tableau T, int row, int col)
            {
                int cols = T.N + T.Slack + 1;
                double piv = T.A[row, col];
                if (Math.Abs(piv) < 1e-14) throw new InvalidOperationException("Pivot too small.");

                for (int j = 0; j < cols; j++) T.A[row, j] /= piv;

                for (int i = 0; i <= T.M; i++)
                {
                    if (i == row) continue;
                    double f = T.A[i, col];
                    if (Math.Abs(f) < 1e-14) continue;
                    for (int j = 0; j < cols; j++)
                        T.A[i, j] -= f * T.A[row, j];
                }

                T.Basis[row] = col;
            }

            public static double[] ReadPrimalSolution(Tableau T, int n)
            {
                int cols = T.N + T.Slack + 1;
                var x = new double[n];
                for (int i = 0; i < T.M; i++)
                {
                    int col = T.Basis[i];
                    if (col < n) x[col] = T.A[i, cols - 1];
                }
                return x;
            }

            /// <summary>
            /// Choose a Gomory row:
            /// 1) candidates = rows with fractional RHS;
            /// 2) prefer those whose BASIC variable is a decision variable (xᵢ);
            /// 3) among them, pick the one with RHS fraction closest to 0.5;
            /// 4) tie-break: leftmost basic column.
            /// If no decision candidate exists, fall back to slacks with the same rule.
            /// </summary>
            public static int ChooseGomoryRowSmart(Tableau T, int n, out string reason)
            {
                int cols = T.N + T.Slack + 1;
                var cand = new List<(int row, int basicCol, bool isX, double frac, double dist)>();
                for (int i = 0; i < T.M; i++)
                {
                    double rhs = T.A[i, cols - 1];
                    double f = Fraction.Frac(rhs);
                    if (f > 1e-9)
                    {
                        int bc = T.Basis[i];
                        bool isX = bc < n;
                        double d = Math.Abs(f - 0.5);
                        cand.Add((i, bc, isX, f, d));
                    }
                }

                if (cand.Count == 0) { reason = "no fractional RHS"; return -1; }

                var xCands = cand.Where(t => t.isX).OrderBy(t => t.dist).ThenBy(t => t.basicCol).ToList();
                var pool = xCands.Count > 0 ? xCands : cand.OrderBy(t => t.dist).ThenBy(t => t.basicCol).ToList();

                if (xCands.Count == 1) reason = "only fraction for the decision variables";
                else if (xCands.Count > 1)
                {
                    double d0 = xCands[0].dist;
                    double d1 = xCands[1].dist;
                    reason = (Math.Abs(d0 - d1) <= 1e-9)
                        ? "both the same distance, so we chose the most left"
                        : "decimal part is closer to 0.5";
                }
                else
                {
                    reason = "no decision variable had a fractional RHS (cutting on slack)";
                }

                return pool[0].row;
            }

            /// <summary>
            /// Build a Gomory cut using fractional parts of **all current columns**
            /// (x and existing slacks), to match the spreadsheet appearance.
            /// Returns (fracCoeffs[0..N+Slack-1], rhsFrac).
            /// </summary>
            public static (double[] FracCoeffsAllCols, double RhsFrac) BuildGomoryCut(Tableau T, int row)
            {
                int totalColsNoRhs = T.N + T.Slack;
                var frac = new double[totalColsNoRhs];
                for (int j = 0; j < totalColsNoRhs; j++)
                    frac[j] = Fraction.Frac(T.A[row, j]);
                double f0 = Fraction.Frac(T.A[row, totalColsNoRhs]);
                return (frac, f0);
            }

            // ===== FULL derivation =====
            public static IEnumerable<string> DeriveCutStepsFull(Tableau T, int row, int n, string cutOn)
            {
                var lines = new List<string>();
                int totalColsNoRhs = T.N + T.Slack;
                int basic = T.Basis[row];
                string basicName = basic < n ? $"x{basic + 1}" : $"s{basic - n + 1}";
                double rhs = T.A[row, totalColsNoRhs];

                // Which columns participate? those with fractional part ≠ 0
                var cols = new List<int>();
                for (int j = 0; j < totalColsNoRhs; j++)
                {
                    if (j == basic) continue;
                    double f = Fraction.Frac(T.A[row, j]);
                    if (f > 1e-12) cols.Add(j);
                }

                // 1) Basic row with RAW coefficients
                var sb1 = new StringBuilder();
                sb1.Append(basicName).Append('\t');
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb1.Append($"{(a >= 0 ? "+ " : "- ")}{FmtFrac(Math.Abs(a))}{name}\t");
                }
                sb1.Append("=\t").Append(FmtFrac(rhs));
                lines.Add(sb1.ToString().TrimEnd());

                // 2) Split integer + fractional pieces
                var sb2 = new StringBuilder();
                sb2.Append(basicName).Append('\t');
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    int k = (int)Math.Floor(a);
                    double f = a - k; if (f < Eps) f = 0;
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb2.Append($"{(k >= 0 ? "+ " : "- ")}{FmtFrac(Math.Abs(k))}{name}\t");
                    if (f > 0) sb2.Append($"+ {FmtFrac(f)}{name}\t");
                }
                int K0 = (int)Math.Floor(rhs);
                double F0 = rhs - K0; if (F0 < Eps) F0 = 0;
                sb2.Append("=\t").Append(FmtFrac(K0)).Append(" + ").Append(FmtFrac(F0));
                lines.Add(sb2.ToString().TrimEnd());

                // 3) Move integers left; fractional to right
                var sb3 = new StringBuilder();
                sb3.Append(basicName).Append('\t');
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    int k = (int)Math.Floor(a);
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb3.Append($"{(k >= 0 ? "+ " : "- ")}{FmtFrac(Math.Abs(k))}{name}\t");
                }
                sb3.Append($"- {FmtFrac(K0)}\t=\t");
                bool first = true;
                if (F0 != 0) { sb3.Append(FmtFrac(F0)); first = false; }
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    int k = (int)Math.Floor(a);
                    double f = a - k; if (f < Eps) f = 0;
                    if (f == 0) continue;
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb3.Append(first ? "" : " ");
                    sb3.Append($"- {FmtFrac(f)}{name}");
                    first = false;
                }
                if (first) sb3.Append("0");
                lines.Add(sb3.ToString());

                // 4) <= 0 form
                var sb4 = new StringBuilder();
                sb4.Append($"+{FmtFrac(F0)}");
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    int k = (int)Math.Floor(a);
                    double f = a - k; if (f < Eps) f = 0;
                    if (f == 0) continue;
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb4.Append($" - {FmtFrac(f)}{name}");
                }
                sb4.Append(" <= 0");
                lines.Add(sb4.ToString());

                // 5) final canonical inequality
                var sb5 = new StringBuilder();
                bool first2 = true;
                foreach (var j in cols)
                {
                    double a = T.A[row, j];
                    int k = (int)Math.Floor(a);
                    double f = a - k; if (f < Eps) f = 0;
                    if (f == 0) continue;
                    string name = j < n ? $"x{j + 1}" : $"s{j - n + 1}";
                    sb5.Append(first2 ? "" : " ");
                    sb5.Append($"- {FmtFrac(f)}{name}");
                    first2 = false;
                }
                if (first2) sb5.Append("0");
                sb5.Append(" <= -").Append(FmtFrac(F0));
                lines.Add(sb5.ToString());

                return lines;
            }

            /// <summary>
            /// Add cut row: - frac(col) + s_new ≤ -f0 across ALL columns (x and slacks),
            /// then objective row moved to the last again.
            /// </summary>
            public static void AddGomoryCut(ref Tableau T, (double[] FracCoeffsAllCols, double RhsFrac) cut)
            {
                int oldRows = T.M + 1;
                int oldCols = T.N + T.Slack + 1; // incl RHS

                var A2 = new double[T.M + 2, T.N + T.Slack + 2]; // +1 slack col, +1 RHS
                for (int i = 0; i < oldRows; i++)
                    for (int j = 0; j < oldCols; j++)
                        A2[i, j] = T.A[i, j];

                int newSlackCol = T.N + T.Slack;
                int r = T.M;

                // - fractional parts on all existing columns (x & slacks)
                for (int j = 0; j < newSlackCol; j++)
                    A2[r, j] = -cut.FracCoeffsAllCols[j];

                // + s_new
                A2[r, newSlackCol] = 1.0;
                // RHS = -f0
                A2[r, newSlackCol + 1] = -cut.RhsFrac;

                // copy objective row down one
                for (int j = 0; j < oldCols; j++)
                    A2[T.M + 1, j] = T.A[T.M, j];
                A2[T.M + 1, newSlackCol] = 0.0;
                A2[T.M + 1, newSlackCol + 1] = T.A[T.M, oldCols - 1];

                var basis = new int[T.M + 1];
                for (int i = 0; i < T.M; i++) basis[i] = T.Basis[i];
                basis[T.M] = newSlackCol;

                T.A = A2;
                T.M = T.M + 1;
                T.Slack = T.Slack + 1;
                T.Basis = basis;
                T.Z = T.A[T.M, T.N + T.Slack];
            }
        }

        // Fraction helpers (pretty formatting like 2 1/4, 5/9, etc.)
        private static class Fraction
        {
            public static string FormatMixed(double x, int maxDen = 99)
            {
                double r = Math.Round(x);
                if (Math.Abs(x - r) < 1e-9) return r.ToString("0", CultureInfo.InvariantCulture);

                int sign = x < 0 ? -1 : 1;
                x = Math.Abs(x);
                int whole = (int)Math.Floor(x);
                double frac = x - whole;
                (int num, int den) = ToFraction(frac, maxDen);
                if (whole == 0)
                    return (sign < 0 ? "-" : "") + $"{num}/{den}";
                else
                    return (sign < 0 ? "-" : "") + $"{whole} {num}/{den}";
            }

            public static double Frac(double x)
            {
                double f = x - Math.Floor(x);
                if (f < 0) f += 1.0;
                if (f < 1e-12) f = 0.0;
                if (1.0 - f < 1e-12) f = 0.0;
                return f;
            }

            private static (int, int) ToFraction(double x, int maxDen)
            {
                int sgn = x < 0 ? -1 : 1; x = Math.Abs(x);
                int a0 = (int)Math.Floor(x);
                double frac = x - a0;
                if (frac < 1e-9) return (a0 * sgn, 1);

                long h1 = 1, k1 = 0, h = a0, k = 1;
                double f = frac;
                for (int i = 0; i < 20; i++)
                {
                    if (Math.Abs(f) < 1e-12) break;
                    int ai = (int)Math.Floor(1.0 / f);
                    long h2 = h1; long k2 = k1; h1 = h; k1 = k;
                    h = ai * h1 + h2; k = ai * k1 + k2;
                    if (k > maxDen) break;
                    f = 1.0 / f - ai;
                }
                int num = (int)(h - a0 * k);
                int den = (int)k;
                if (den == 0) return (a0 * sgn, 1);
                if (num == 0) return (0, 1);
                int g = Gcd(Math.Abs(num), Math.Abs(den));
                return (num / g, den / g);
            }

            private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
        }
    }
}
