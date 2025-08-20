using System;
using System.Collections.Generic;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.IO;
using static Solver.Engine.Core.Numeric;
using static Solver.Engine.Simplex.TableauOps;

namespace Solver.Engine.Simplex
{
    public sealed class DualSimplexSolver : ISolver
    {
        public string Name => "Dual Simplex (Tableau)";

        private const int MAX_ITERS = 10000;
        private static readonly ITableauPrinter Printer = new DefaultTableauPrinter();

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();

            // Canonicalize (Min -> dual setup is handled in Canonicalizer)
            var canRes = Canonicalizer.ToCanonical(model);
            var can0 = canRes.Canonical;

            if (canRes.Log is { Count: > 0 })
            {
                log.Add("— Canonicalization steps —");
                log.AddRange(canRes.Log);
                log.Add("");
            }

            // Phase I if artificials are present (copied/adapted from Primal)
            CanonicalForm can;
            if (can0.PhaseIRequired)
            {
                var r1 = RunPhaseI(can0, log);
                if (!r1.Success)
                {
                    log.Add("Phase I failed — infeasible.");
                    return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
                }
                can = r1.CanonPhaseII;
                log.Add("— Phase I complete — artificials removed.");
                log.Add(Printer.RenderCanonical(can.A, can.b, can.c, can.NumVarsOriginal, can.Map.ColumnNames, can.Map.RowNames));
            }
            else
            {
                can = can0;
            }

            // Build Phase II tableau
            int m = can.A.GetLength(0);
            int n = can.A.GetLength(1);
            int p = can.NumVarsOriginal;      // original decision vars
            int width = n + 1;

            var T = new double[m + 1, n + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                T[i + 1, n] = can.b[i];    // RHS (may be negative for dual start)
            }
            for (int j = 0; j < n; j++) T[0, j] = -can.c[j];   // z-row
            T[0, n] = can.z0;

            // Basis
            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
            if (!IsValidIdentityBasis(T, basis, m, n)) basis = DetectIdentityBasis(T, m, n);
            if (!IsValidIdentityBasis(T, basis, m, n))
            {
                log.Add("No valid initial identity basis (unexpected).");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }
            var basicSet = new HashSet<int>(basis);

            // Canonicalize z-row (reduced costs)
            for (int r = 0; r < m; r++)
            {
                double cb = can.c[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            log.Add(Printer.RenderCanonical(can.A, can.b, can.c, p, can.Map.ColumnNames, can.Map.RowNames));
            log.Add(Printer.Render("t-1 (dual start)", T, m, n, basis, p, -1, new double[m], can.Map.ColumnNames, can.Map.RowNames));

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                // 1) Leaving row: most negative RHS
                int leave = FindLeavingRowDual(T, m, n, basis);
                if (leave == -1)
                {
                    // Primal feasible now. Check reduced costs too.
                    if (HasNegativeReducedCost(T, basicSet, n))
                    {
                        // Finish with a short primal loop from current tableau/basis.
                        FinishWithPrimalFromHere(T, ref basis, basicSet, can, p, width, log);
                    }

                    // Now optimal.
                    double z = T[0, n];
                    var xFull = ExtractPrimal(T, m, n, basis);
                    var xDecision = xFull.Take(Math.Min(p, xFull.Length)).ToArray();
                    log.Add("Optimality reached (Dual Simplex).");
                    return new SolverResult(true, z, xDecision, log);
                }

                // 2) Entering column: minimize z_j / a_rj over a_rj < 0
                int enter = FindEnteringColDual(T, leave, basicSet, n);
                if (enter == -1)
                {
                    log.Add($"Infeasible: row {NiceRowName(leave, can.Map.RowNames)} has RHS < 0 but no a_rj < 0.");
                    return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
                }

                log.Add($"--- Iteration {iter}: leave {NiceRowName(leave, can.Map.RowNames)}, enter {NiceColName(enter, can.Map.ColumnNames, p)} ---");

                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);

                log.Add(Printer.Render("t-1", T, m, n, basis, p, -1, new double[m], can.Map.ColumnNames, can.Map.RowNames));
            }

            log.Add("Max iterations exceeded.");
            return new SolverResult(false, 0, Array.Empty<double>(), log);
        }

        // ======== Dual rules ========

        // most negative RHS (choose smallest RHS; tie-break by larger basis index for stability)
        private static int FindLeavingRowDual(double[,] T, int m, int n, int[] basis)
        {
            int leave = -1;
            double worst = 0.0;
            for (int r = 0; r < m; r++)
            {
                double rhs = T[r + 1, n];
                if (rhs < worst - EPS ||
                    (leave == -1 && rhs < -EPS) ||
                    (Math.Abs(rhs - worst) <= EPS && leave >= 0 && basis[r] > basis[leave]))
                {
                    worst = rhs;
                    leave = r;
                }
            }
            return leave;
        }

        // minimize z_j / a_rj over a_rj < 0 and j nonbasic
        private static int FindEnteringColDual(double[,] T, int leave, HashSet<int> basicSet, int n)
        {
            int enter = -1;
            double best = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                double a = T[leave + 1, j];
                if (a < -EPS)
                {
                    double ratio = T[0, j] / a;
                    if (ratio < best - EPS || (Math.Abs(ratio - best) <= EPS && (enter == -1 || j > enter)))
                    {
                        best = ratio;
                        enter = j;
                    }
                }
            }
            return enter;
        }

        private static bool HasNegativeReducedCost(double[,] T, HashSet<int> basicSet, int n)
        {
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                if (T[0, j] < -EPS) return true;
            }
            return false;
        }

        // ======== quick primal finish if RHS feasible but rc still negative ========

        private static void FinishWithPrimalFromHere(double[,] T, ref int[] basis, HashSet<int> basicSet,
                                                     CanonicalForm can, int p, int width, List<string> log)
        {
            int m = can.NumRows;
            int n = can.NumCols;

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                int enter = FindEnteringPrimal(T, basicSet, n);
                if (enter == -1) break; // rc ≥ 0 → done

                int leave = -1;
                double minRatio = double.PositiveInfinity;
                for (int r = 0; r < m; r++)
                {
                    double a = T[r + 1, enter];
                    if (a > EPS)
                    {
                        double ratio = T[r + 1, n] / a;
                        if (ratio < minRatio - EPS ||
                            (Math.Abs(ratio - minRatio) <= EPS && (leave == -1 || basis[r] > basis[leave])))
                        {
                            minRatio = ratio;
                            leave = r;
                        }
                    }
                }
                if (leave == -1)
                {
                    log.Add($"Unbounded during primal finish (enter col {enter + 1}).");
                    break;
                }

                log.Add($"Primal-finish: enter {NiceColName(enter, can.Map.ColumnNames, p)}, leave {NiceRowName(leave, can.Map.RowNames)}");
                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);
            }
        }

        private static int FindEnteringPrimal(double[,] T, HashSet<int> basicSet, int n)
        {
            int enter = -1;
            double best = 0.0;
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                double rc = T[0, j];
                if (rc < best - EPS) { best = rc; enter = j; } // standard primal rule for Max
            }
            return enter;
        }

        // ======== helpers (same as primal) ========

        private static bool IsValidIdentityBasis(double[,] T, int[] basis, int m, int n)
        {
            if (basis == null || basis.Length != m) return false;
            for (int r = 0; r < m; r++)
            {
                int j = basis[r];
                if (j < 0 || j >= n) return false;
                if (Math.Abs(T[r + 1, j] - 1.0) > EPS) return false;
                for (int rr = 0; rr < m; rr++)
                    if (rr != r && Math.Abs(T[rr + 1, j]) > EPS) return false;
            }
            return true;
        }

        private static int[] DetectIdentityBasis(double[,] T, int m, int n)
        {
            var b = Enumerable.Repeat(-1, m).ToArray();
            for (int r = 0; r < m; r++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (Math.Abs(T[r + 1, j] - 1.0) > EPS) continue;
                    bool ok = true;
                    for (int rr = 0; rr < m; rr++)
                        if (rr != r && Math.Abs(T[rr + 1, j]) > EPS) { ok = false; break; }
                    if (ok) { b[r] = j; break; }
                }
            }
            return b;
        }

        private static double[] ExtractPrimal(double[,] T, int m, int n, int[] basis)
        {
            var x = new double[n];
            for (int r = 0; r < m; r++)
            {
                int j = basis[r];
                if (j >= 0 && j < n) x[j] = T[r + 1, n];
            }
            return x;
        }

        private static string NiceColName(int j, string[]? colNames, int numVars)
        {
            if (colNames is { Length: > 0 } && j >= 0 && j < colNames.Length) return colNames[j];
            return j < numVars ? $"x{j + 1}" : $"s{j - numVars + 1}";
        }

        private static string NiceRowName(int i, string[]? rowNames)
            => (rowNames is { Length: > 0 } && i >= 0 && i < rowNames.Length) ? rowNames[i] : $"c{i + 1}";

        // ======== Phase I (from primal) ========

        private sealed class PhaseIResult
        {
            public bool Success { get; init; }
            public CanonicalForm CanonPhaseII { get; init; } = new CanonicalForm();
        }

        private PhaseIResult RunPhaseI(CanonicalForm can, List<string> log)
        {
            log.Add("— Phase I — Build and solve auxiliary problem (maximize -Σ a_i).");

            int m = can.A.GetLength(0);
            int n = can.A.GetLength(1);
            int width = n + 1;

            var T = new double[m + 1, n + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                T[i + 1, n] = can.b[i];
            }
            for (int j = 0; j < n; j++) T[0, j] = -can.cPhaseI[j];
            T[0, n] = 0.0;

            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
            if (!IsValidIdentityBasis(T, basis, m, n)) basis = DetectIdentityBasis(T, m, n);
            if (!IsValidIdentityBasis(T, basis, m, n))
            {
                log.Add("Phase I: Could not find an identity basis in the initial tableau.");
                return new PhaseIResult { Success = false };
            }
            var basicSet = new HashSet<int>(basis);

            for (int r = 0; r < m; r++)
            {
                double cb = can.cPhaseI[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            int enter = FindEnteringPhaseI(T, basicSet, n);
            var thetaDisp = ComputeThetaColumn(T, enter, m, n);
            log.Add(Printer.Render("Phase I — t-1", T, m, n, basis, can.NumVarsOriginal, enter, thetaDisp, can.Map.ColumnNames, can.Map.RowNames));

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                enter = FindEnteringPhaseI(T, basicSet, n);
                if (enter == -1)
                {
                    if (Math.Abs(T[0, n]) > 1e-7)
                    {
                        log.Add($"Phase I objective z={T[0, n]:0.###} ≠ 0 ⇒ infeasible.");
                        return new PhaseIResult { Success = false };
                    }
                    break;
                }

                int leave = -1;
                double minRatio = double.PositiveInfinity;
                for (int r = 0; r < m; r++)
                {
                    double a = T[r + 1, enter];
                    if (a > EPS)
                    {
                        double ratio = T[r + 1, n] / a;
                        if (ratio < minRatio - EPS ||
                            (Math.Abs(ratio - minRatio) <= EPS && (leave == -1 || basis[r] > basis[leave])))
                        {
                            minRatio = ratio; leave = r;
                        }
                    }
                }
                if (leave == -1)
                {
                    log.Add($"Phase I: unbounded ascent with entering col {enter + 1} (unexpected).");
                    return new PhaseIResult { Success = false };
                }

                log.Add($"Phase I — Iter {iter}: enter {NiceColName(enter, can.Map.ColumnNames, can.NumVarsOriginal)}, leave {NiceRowName(leave, can.Map.RowNames)}");
                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);

                int nextEnter = FindEnteringPhaseI(T, basicSet, n);
                var thetaNext = ComputeThetaColumn(T, nextEnter, m, n);
                log.Add(Printer.Render("Phase I — t-1", T, m, n, basis, can.NumVarsOriginal, nextEnter, thetaNext, can.Map.ColumnNames, can.Map.RowNames));
            }

            // drop artificials
            var artSet = new HashSet<int>(can.ArtificialIdx ?? Array.Empty<int>());
            var keepCols = Enumerable.Range(0, n).Where(j => !artSet.Contains(j)).ToArray();
            int n2 = keepCols.Length;

            var A2 = new double[m, n2];
            for (int i = 0; i < m; i++)
                for (int jj = 0; jj < n2; jj++)
                    A2[i, jj] = T[i + 1, keepCols[jj]];

            var b2 = new double[m];
            for (int i = 0; i < m; i++) b2[i] = T[i + 1, n];

            var pos = new Dictionary<int, int>();
            for (int jj = 0; jj < n2; jj++) pos[keepCols[jj]] = jj;

            var basis2 = new int[m];
            for (int i = 0; i < m; i++)
            {
                int jOld = basis[i];
                basis2[i] = pos.ContainsKey(jOld) ? pos[jOld] : -1;
            }

            var c2 = new double[n2];
            for (int jj = 0; jj < n2; jj++) c2[jj] = can.c[keepCols[jj]];

            var colNames2 = (can.Map.ColumnNames is { Length: > 0 })
                ? keepCols.Select(j => can.Map.ColumnNames[j]).ToArray()
                : Enumerable.Range(1, n2).Select(j => j <= can.NumVarsOriginal ? $"x{j}" : $"s{j - can.NumVarsOriginal}").ToArray();

            var rowNames2 = can.Map.RowNames is { Length: > 0 } ? can.Map.RowNames : Enumerable.Range(1, m).Select(i => $"c{i}").ToArray();

            int[] slackNew = can.SlackIdx?.Where(j => pos.ContainsKey(j)).Select(j => pos[j]).ToArray() ?? Array.Empty<int>();
            int[] surplusNew = can.SurplusIdx?.Where(j => pos.ContainsKey(j)).Select(j => pos[j]).ToArray() ?? Array.Empty<int>();

            var rowToAdded2 = new int[m][];
            for (int i = 0; i < m; i++)
            {
                var list = new List<int>();
                foreach (var j in slackNew) if (Math.Abs(A2[i, j]) > EPS) list.Add(j);
                foreach (var j in surplusNew) if (Math.Abs(A2[i, j]) > EPS) list.Add(j);
                rowToAdded2[i] = list.ToArray();
            }

            var nameMap2 = new NameMap
            {
                ColumnNames = colNames2,
                RowNames = rowNames2,
                VarToColumns = can.Map.VarToColumns,
                RowToAddedColumns = rowToAdded2
            };

            return new PhaseIResult
            {
                Success = true,
                CanonPhaseII = new CanonicalForm(A2, b2, c2, 0.0, basis2, Enumerable.Range(0, n2).Except(basis2).ToArray())
                {
                    PhaseIRequired = false,
                    cPhaseI = Array.Empty<double>(),
                    ArtificialIdx = Array.Empty<int>(),
                    SlackIdx = slackNew,
                    SurplusIdx = surplusNew,
                    NumRows = m,
                    NumCols = n2,
                    NumVarsOriginal = can.NumVarsOriginal,
                    NumSlack = slackNew.Length,
                    NumArtificial = 0,
                    Map = nameMap2
                }
            };
        }

        private static int FindEnteringPhaseI(double[,] T, HashSet<int> basicSet, int n)
        {
            int enter = -1;
            double best = 0.0;
            for (int j = 0; j < n; j++)
            {
                if (basicSet.Contains(j)) continue;
                double rc = T[0, j];
                if (rc < best - EPS) { best = rc; enter = j; }
            }
            return enter;
        }

        private static double[] ComputeThetaColumn(double[,] T, int enter, int m, int n)
        {
            var theta = new double[m];
            if (enter < 0) return theta;
            for (int r = 0; r < m; r++)
            {
                double a = T[r + 1, enter];
                theta[r] = Math.Abs(a) < EPS ? double.NaN : T[r + 1, n] / a;
            }
            return theta;
        }
    }
}
