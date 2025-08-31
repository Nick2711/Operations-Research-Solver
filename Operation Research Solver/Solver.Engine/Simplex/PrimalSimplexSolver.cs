using System;
using System.Collections.Generic;
using System.Linq;
using Solver.Engine.Core;
using Solver.Engine.IO;
using Solver.Engine.Sensitivity;
using static Solver.Engine.Core.Numeric;
using static Solver.Engine.Simplex.TableauOps;
using Solver.Engine.Utils;


namespace Solver.Engine.Simplex
{
    public sealed class PrimalSimplexSolver : ISolver
    {
        public string Name => "Primal Simplex (Tableau)";

        private const int MAX_ITERS = 10000;
        private static readonly ITableauPrinter Printer = new DefaultTableauPrinter();

        public SolverResult Solve(LpModel model)
        {
            var log = new List<string>();

            // Canonicalize
            var canRes = Canonicalizer.ToCanonical(model);
            var can = canRes.Canonical;

            if (canRes.Log is { Count: > 0 })
            {
                log.Add("— Canonicalization steps —");
                log.AddRange(canRes.Log);
                log.Add("");
            }

            // Phase I if required
            if (can.PhaseIRequired)
            {
                var phaseI = RunPhaseI(can, log);
                if (!phaseI.Success)
                    return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);

                can = phaseI.CanonPhaseII;
            }

            // Phase II (regular)
            return RunPhaseII(model, can, log);
        }

        private SolverResult RunPhaseII(LpModel model, CanonicalForm can, List<string> log)
        {
            int m = can.A.GetLength(0);
            int n = can.A.GetLength(1);
            int p = model.NumVars;          // original decision count
            int width = n + 1;

            var T = new double[m + 1, n + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++) T[i + 1, j] = can.A[i, j];
                T[i + 1, n] = can.b[i];
            }
            for (int j = 0; j < n; j++) T[0, j] = -can.c[j];
            T[0, n] = can.z0;

            // Basis
            var basis = (can.BasicIdx ?? Array.Empty<int>()).ToArray();
            if (!IsValidIdentityBasis(T, basis, m, n)) basis = DetectIdentityBasis(T, m, n);
            if (!IsValidIdentityBasis(T, basis, m, n))
            {
                log.Add("No valid initial identity basis; Phase I would be required (but failed to produce one).");
                return new SolverResult(false, 0, Array.Empty<double>(), log, infeasible: true);
            }
            var basicSet = new HashSet<int>(basis);

            // Canonicalize z-row
            for (int r = 0; r < m; r++)
            {
                double cb = can.c[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            // Print canonical + initial tableau with accurate names
            log.Add(Printer.RenderCanonical(can.A, can.b, can.c, p, can.Map.ColumnNames, can.Map.RowNames));
            int enter = FindEntering(T, basicSet, n);
            var thetaDisp = ComputeThetaColumn(T, enter, m, n);
            log.Add(Printer.Render("t-1", T, m, n, basis, p, enter, thetaDisp, can.Map.ColumnNames, can.Map.RowNames));

            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                enter = FindEntering(T, basicSet, n);
                if (enter == -1)
                {
                    double z = T[0, n];
                    var xFull = ExtractPrimal(T, m, n, basis);
                    var xDecision = xFull.Take(Math.Min(model.NumVars, xFull.Length)).ToArray();


                    // ---- Build sensitivity payload from current basis/tableau ----
                    int[] nonbasic = Enumerable.Range(0, n).Except(basis).ToArray();

                    // B from the original (Phase II) A matrix
                    var B = new double[m, m];
                    for (int i = 0; i < m; i++)
                        for (int j = 0; j < m; j++)
                            B[i, j] = can.A[i, basis[j]];

                    // N from original A
                    var Nmat = new double[m, n - m];
                    for (int i = 0; i < m; i++)
                        for (int jj = 0; jj < nonbasic.Length; jj++)
                            Nmat[i, jj] = can.A[i, nonbasic[jj]];

                    // Costs split by basis/nonbasis
                    var cB = basis.Select(j => can.c[j]).ToArray();
                    var cN = nonbasic.Select(j => can.c[j]).ToArray();

                    // RHS from the current tableau (these are basic values)
                    var bVec = new double[m];
                    for (int i = 0; i < m; i++) bVec[i] = T[i + 1, n];

                    // ❗ Compute B^{-1}
                    var BInv = Matrix.Invert(B);

                    // ❗ Shadow prices: y^T = c_B^T B^{-1}  -> y_i = sum_k cB[k] * BInv[k,i]
                    var y = new double[m];
                    for (int i = 0; i < m; i++)
                    {
                        double s = 0;
                        for (int k = 0; k < m; k++) s += cB[k] * BInv[k, i];
                        y[i] = s;
                    }

                    var payload = new Solver.Engine.Core.SensitivityPayload(
                        B, BInv, Nmat, cB, cN, bVec, basis.ToArray(), nonbasic, y
                    );

                    // ---- Return with sensitivity payload attached ----
                    log.Add("Optimality reached.");
                    return new SolverResult(true, z, xDecision, log)
                    {
                        Sensitivity = payload
                    };


                    // ------------------------------------------------


                    /*
                    log.Add("Optimality reached.");
                    return new SolverResult(true, z, xDecision, log);*/
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
                            minRatio = ratio;
                            leave = r;
                        }
                    }
                }
                if (leave == -1)
                {
                    log.Add($"Unbounded: entering {NiceColName(enter, can.Map.ColumnNames, p)} has no positive entries.");
                    return new SolverResult(false, 0, Array.Empty<double>(), log, unbounded: true);
                }

                log.Add($"--- Iteration {iter}: enter {NiceColName(enter, can.Map.ColumnNames, p)}, leave {NiceRowName(leave, can.Map.RowNames)} ---");

                Pivot(T, leave + 1, enter, width);
                basicSet.Remove(basis[leave]);
                basis[leave] = enter;
                basicSet.Add(enter);

                int nextEnter = FindEntering(T, basicSet, n);
                var thetaNext = ComputeThetaColumn(T, nextEnter, m, n);
                log.Add(Printer.Render("t-1", T, m, n, basis, p, nextEnter, thetaNext, can.Map.ColumnNames, can.Map.RowNames));
            }

            log.Add("Max iterations exceeded.");
            return new SolverResult(false, 0, Array.Empty<double>(), log);
        }

        // ---------- Phase I runner ----------
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

            // Build tableau for Phase I
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

            // Canonicalize z-row under Phase I costs
            for (int r = 0; r < m; r++)
            {
                double cb = can.cPhaseI[basis[r]];
                if (Math.Abs(cb) > EPS) AddScaledRow(T, 0, r + 1, cb, width);
            }

            int enter = FindEntering(T, basicSet, n);
            var thetaDisp = ComputeThetaColumn(T, enter, m, n);
            log.Add(Printer.Render("Phase I — t-1", T, m, n, basis, can.NumVarsOriginal, enter, thetaDisp, can.Map.ColumnNames, can.Map.RowNames));

            // Iterate
            int iter = 0;
            while (iter++ < MAX_ITERS)
            {
                enter = FindEntering(T, basicSet, n);
                if (enter == -1)
                {
                    double zI = T[0, n];
                    log.Add($"Phase I optimum reached. z_I = {F(zI)}");
                    // Because we maximize -Σ a_i, feasibility means zI == 0 (within EPS)
                    if (zI < -EPS)
                    {
                        log.Add("Phase I indicates infeasibility (z_I < 0).");
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
                            minRatio = ratio;
                            leave = r;
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

                int nextEnter = FindEntering(T, basicSet, n);
                var thetaNext = ComputeThetaColumn(T, nextEnter, m, n);
                log.Add(Printer.Render("Phase I — t-1", T, m, n, basis, can.NumVarsOriginal, nextEnter, thetaNext, can.Map.ColumnNames, can.Map.RowNames));
            }

            // Pivot artificials out if any remain in basis
            var artSet = new HashSet<int>(can.ArtificialIdx ?? Array.Empty<int>());
            for (int r = 0; r < m; r++)
            {
                if (!artSet.Contains(basis[r])) continue;

                int pcol = -1;
                for (int j = 0; j < n; j++)
                {
                    if (artSet.Contains(j)) continue;
                    if (Math.Abs(T[r + 1, j]) > EPS) { pcol = j; break; }
                }
                if (pcol == -1)
                {
                    log.Add($"Phase I: could not pivot artificial out from row {NiceRowName(r, can.Map.RowNames)} — degenerate constraint.");
                    return new PhaseIResult { Success = false };
                }

                Pivot(T, r + 1, pcol, width);
                basis[r] = pcol;
            }

            // Remove artificial columns
            var keepCols = Enumerable.Range(0, n).Where(j => !artSet.Contains(j)).ToArray();
            int n2 = keepCols.Length;
            var A2 = new double[m, n2];
            for (int i = 0; i < m; i++)
                for (int jj = 0; jj < n2; jj++)
                    A2[i, jj] = T[i + 1, keepCols[jj]];

            var b2 = new double[m];
            for (int i = 0; i < m; i++) b2[i] = T[i + 1, n];

            // Map basis to new col indices
            var pos = new Dictionary<int, int>();
            for (int jj = 0; jj < n2; jj++) pos[keepCols[jj]] = jj;

            var basis2 = new int[m];
            for (int i = 0; i < m; i++)
            {
                if (!pos.TryGetValue(basis[i], out int colNew))
                {
                    log.Add($"Phase I: internal error mapping basis column {basis[i]} after removing artificials.");
                    return new PhaseIResult { Success = false };
                }
                basis2[i] = colNew;
            }

            // Build Phase II objective over reduced columns
            var c2 = new double[n2];
            for (int jj = 0; jj < n2; jj++) c2[jj] = can.c[keepCols[jj]];

            // Remap names (drop artificials)
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

            var nonBasic2 = Enumerable.Range(0, n2).Except(basis2).ToArray();

            var can2 = new CanonicalForm(A2, b2, c2, 0.0, basis2, nonBasic2)
            {
                PhaseIRequired = false,
                NumRows = m,
                NumCols = n2,
                NumVarsOriginal = can.NumVarsOriginal,
                NumSlack = slackNew.Length,
                NumArtificial = 0,
                SlackIdx = slackNew,
                SurplusIdx = surplusNew,
                ArtificialIdx = Array.Empty<int>(),
                Map = nameMap2
            };

            log.Add("Phase I: artificials eliminated; proceeding to Phase II.");
            return new PhaseIResult { Success = true, CanonPhaseII = can2 };
        }

        // ==== helpers ====
        private static string NiceColName(int j, string[]? colNames, int numVars)
        {
            if (colNames is { Length: > 0 } && j >= 0 && j < colNames.Length) return colNames[j];
            return j < numVars ? $"x{j + 1}" : $"s{j - numVars + 1}";
        }

        private static string NiceRowName(int i, string[]? rowNames)
            => (rowNames is { Length: > 0 } && i >= 0 && i < rowNames.Length) ? rowNames[i] : $"c{i + 1}";

        private static int FindEntering(double[,] T, HashSet<int> basicSet, int n)
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
    }
}
