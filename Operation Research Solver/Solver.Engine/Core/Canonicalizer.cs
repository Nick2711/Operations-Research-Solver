using System;
using System.Collections.Generic;
using System.Linq;

namespace Solver.Engine.Core
{
    public static class Canonicalizer
    {
        public sealed class Result
        {
            public CanonicalForm Canonical { get; }
            public List<string> Log { get; } = new(); // steps taken
            public Result(CanonicalForm c) => Canonical = c;
        }

        public static Result ToCanonical(LpModel model)
        {
            var log = new List<string>();

            // Step 1: ensure Max
            double dirSign = model.Direction == OptimizeDirection.Max ? 1.0 : -1.0;
            log.Add(model.Direction == OptimizeDirection.Max
                ? "Objective: already Max(Z) — no flip."
                : "Objective: Min detected — multiplying objective by -1 to convert to Max(Z).");

            // Step 2: expand variables to nonnegatives
            var varMap = new List<(int originalIndex, int[] newIndices, double[] multipliers)>();
            var cList = new List<double>();
            int newVarCount = 0;
            for (int j = 0; j < model.NumVars; j++)
            {
                var v = model.Variables[j];
                switch (v.Sign)
                {
                    case SignRestriction.Plus:
                        varMap.Add((j, new[] { newVarCount }, new[] { 1.0 }));
                        cList.Add(dirSign * v.ObjectiveCoeff);
                        log.Add($"Var x{j + 1}: Plus (x ≥ 0) → keep as col {newVarCount + 1}.");
                        newVarCount++;
                        break;

                    case SignRestriction.Minus:
                        // x = -y, y ≥ 0
                        varMap.Add((j, new[] { newVarCount }, new[] { -1.0 }));
                        cList.Add(dirSign * v.ObjectiveCoeff * (-1.0));
                        log.Add($"Var x{j + 1}: Minus → substitute x = -y (col {newVarCount + 1}).");
                        newVarCount++;
                        break;

                    case SignRestriction.Urs:
                        // x = x⁺ - x⁻
                        varMap.Add((j, new[] { newVarCount, newVarCount + 1 }, new[] { 1.0, -1.0 }));
                        cList.Add(dirSign * v.ObjectiveCoeff);      // x+
                        cList.Add(dirSign * (-v.ObjectiveCoeff));   // x-
                        log.Add($"Var x{j + 1}: URS → split to x⁺ (col {newVarCount + 1}) and x⁻ (col {newVarCount + 2}).");
                        newVarCount += 2;
                        break;

                    case SignRestriction.Int:
                    case SignRestriction.Bin:
                        // Continuous relaxation here; B&B will enforce integrality later.
                        varMap.Add((j, new[] { newVarCount }, new[] { 1.0 }));
                        cList.Add(dirSign * v.ObjectiveCoeff);
                        log.Add($"Var x{j + 1}: {v.Sign} → LP relaxation (x ≥ 0) at col {newVarCount + 1}.");
                        newVarCount++;
                        break;

                    default:
                        throw new NotSupportedException("Unknown sign restriction");
                }
            }

            // Step 3: build rows, normalize RHS >= 0 (flip row & relation if needed)
            var rows = new List<double[]>();
            var b = new List<double>();
            var rels = new List<Relation>();
            for (int ci = 0; ci < model.Constraints.Count; ci++)
            {
                var ct = model.Constraints[ci];
                var row = new double[newVarCount];
                Array.Fill(row, 0.0);

                // expand coefficients
                for (int j = 0; j < model.NumVars; j++)
                {
                    var (_, idxs, mults) = varMap[j];
                    for (int k = 0; k < idxs.Length; k++)
                        row[idxs[k]] += ct.Coeffs[j] * mults[k];
                }

                double rhs = ct.Rhs;
                var rel = ct.Relation;

                // normalize RHS >= 0
                if (rhs < 0)
                {
                    for (int k = 0; k < row.Length; k++) row[k] *= -1.0;
                    rhs *= -1.0;
                    rel = rel switch
                    {
                        Relation.LessOrEqual => Relation.GreaterOrEqual,
                        Relation.GreaterOrEqual => Relation.LessOrEqual,
                        _ => Relation.Equal
                    };
                    log.Add($"c{ci + 1}: RHS < 0 → multiply row by -1; relation flipped to {PrettyRel(rel)}; RHS={rhs}.");
                }

                rows.Add(row);
                b.Add(rhs);
                rels.Add(rel);
            }

            // Step 4: add slacks / surplus / artificials
            int m = rows.Count;
            int slackCount = 0, surplusCount = 0, artCount = 0;

            var slackIdx = new List<int>();
            var surplusIdx = new List<int>();
            var artIdx = new List<int>();
            var basic = new int[m];

            int col = newVarCount; // next free column after expanded decision vars
            var augRows = rows.Select(r => r.ToList()).ToList();

            for (int i = 0; i < m; i++)
            {
                switch (rels[i])
                {
                    case Relation.LessOrEqual:
                        {
                            int colBefore = col;
                            foreach (var r in augRows) r.Add(0.0);
                            augRows[i][col] = 1.0;

                            slackIdx.Add(col);
                            basic[i] = col;
                            slackCount++;
                            col++;

                            log.Add($"c{i + 1}: '≤' → add slack s{i + 1} as basis (col {colBefore + 1}).");
                            break;
                        }

                    case Relation.Equal:
                        {
                            int colBefore = col;
                            foreach (var r in augRows) r.Add(0.0);
                            augRows[i][col] = 1.0;

                            artIdx.Add(col);
                            basic[i] = col;
                            artCount++;
                            col++;

                            log.Add($"c{i + 1}: '=' → add artificial a{i + 1} as basis (col {colBefore + 1}).");
                            break;
                        }

                    case Relation.GreaterOrEqual:
                        {
                            // surplus r_i with -1 on row i
                            int colSur = col;
                            foreach (var r in augRows) r.Add(0.0);
                            augRows[i][col] = -1.0;
                            surplusIdx.Add(col);
                            surplusCount++;
                            col++;

                            // artificial a_i with +1 on row i (enters basis)
                            int colArt = col;
                            foreach (var r in augRows) r.Add(0.0);
                            augRows[i][col] = 1.0;
                            artIdx.Add(col);
                            basic[i] = col;
                            artCount++;
                            col++;

                            log.Add($"c{i + 1}: '≥' → add surplus (col {colSur + 1}) and artificial (basis, col {colArt + 1}).");
                            break;
                        }

                    default:
                        throw new InvalidOperationException("Unknown relation.");
                }
            }

            int n = augRows[0].Count;
            var A = new double[m, n];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                    A[i, j] = augRows[i][j];

            // Phase II objective (original) — 0 for added cols
            var c = new double[n];
            for (int j = 0; j < newVarCount; j++) c[j] = cList[j];

            // Phase I objective: minimize sum(artificials) → in Max form set -1 on artificial columns
            var cPhaseI = new double[n];
            foreach (var j in artIdx) cPhaseI[j] = -1.0;

            // nonbasic set
            var nonBasic = Enumerable.Range(0, n).Except(basic).ToArray();

            // Names (columns, rows)
            var colNames = new string[n];
            for (int j = 0; j < newVarCount; j++) colNames[j] = $"x{j + 1}";
            for (int k = 0; k < slackIdx.Count; k++) colNames[slackIdx[k]] = $"s{k + 1}";
            for (int k = 0; k < surplusIdx.Count; k++) colNames[surplusIdx[k]] = $"r{k + 1}";
            for (int k = 0; k < artIdx.Count; k++) colNames[artIdx[k]] = $"a{k + 1}";
            var rowNames = Enumerable.Range(1, m).Select(i => $"c{i}").ToArray();

            // Build name map (after A is finalized)
            var rowToAdded = new int[m][];
            for (int i = 0; i < m; i++)
            {
                var addedCols = new List<int>();
                foreach (var j in slackIdx) if (Math.Abs(A[i, j]) > 0.5) addedCols.Add(j);
                foreach (var j in surplusIdx) if (Math.Abs(A[i, j]) > 0.5) addedCols.Add(j);
                foreach (var j in artIdx) if (Math.Abs(A[i, j]) > 0.5) addedCols.Add(j);
                rowToAdded[i] = addedCols.ToArray();
            }

            var nameMap = new NameMap
            {
                ColumnNames = colNames,
                RowNames = rowNames,
                VarToColumns = varMap.Select(t => t.newIndices).ToArray(),
                RowToAddedColumns = rowToAdded
            };

            // Summaries
            bool phaseIRequired = artIdx.Count > 0;
            log.Add($"Canonicalized: m={m}, n={n} (vars={newVarCount}, slacks={slackCount}, surplus={surplusCount}, artificials={artCount}).");
            log.Add($"Basis: {(phaseIRequired ? "artificials/slacks" : "slacks only")}. z0 = 0.");
            if (phaseIRequired) log.Add("Phase I is required (artificials present).");

            // Build CanonicalForm with back-compat ctor, then fill Phase-I metadata
            var cf = new CanonicalForm(A, b.ToArray(), c, 0.0, basic, nonBasic)
            {
                PhaseIRequired = phaseIRequired,
                cPhaseI = cPhaseI,
                ArtificialIdx = artIdx.ToArray(),
                SlackIdx = slackIdx.ToArray(),
                SurplusIdx = surplusIdx.ToArray(),
                NumRows = m,
                NumCols = n,
                NumVarsOriginal = model.NumVars,
                NumSlack = slackCount,
                NumArtificial = artCount,
                Map = nameMap
            };

            var result = new Result(cf);
            result.Log.AddRange(log);
            return result;
        }

        private static string PrettyRel(Relation r) => r switch
        {
            Relation.LessOrEqual => "≤",
            Relation.GreaterOrEqual => "≥",
            Relation.Equal => "=",
            _ => "?"
        };
    }
}
