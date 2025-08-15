using Solver.Engine.Core;

namespace Solver.Enginer.Engine.Core;

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
        // Step 1: ensure Max
        double dirSign = model.Direction == OptimizeDirection.Max ? 1.0 : -1.0;

        // Step 2: build variable mapping to nonnegative vars (expand URS and Minus)
        // For now: treat Plus (+) as x >= 0; Minus (-) substitute x = -y (y >= 0); URS substitute x = x+ - x-.
        var map = new List<(int originalIndex, int[] newIndices, double[] multipliers)>(); // each original var maps to new nonnegatives
        var cList = new List<double>();
        int newVarCount = 0;

        for (int j = 0; j < model.NumVars; j++)
        {
            var v = model.Variables[j];
            switch (v.Sign)
            {
                case SignRestriction.Plus:
                    map.Add((j, new[] { newVarCount }, new[] { 1.0 }));
                    cList.Add(dirSign * v.ObjectiveCoeff);
                    newVarCount++;
                    break;

                case SignRestriction.Minus:
                    // x = -y, y >= 0
                    map.Add((j, new[] { newVarCount }, new[] { -1.0 }));
                    cList.Add(dirSign * v.ObjectiveCoeff * (-1.0));
                    newVarCount++;
                    break;

                case SignRestriction.Urs:
                    // x = x+ - x-
                    map.Add((j, new[] { newVarCount, newVarCount + 1 }, new[] { 1.0, -1.0 }));
                    cList.Add(dirSign * v.ObjectiveCoeff);   // coef for x+
                    cList.Add(dirSign * (-v.ObjectiveCoeff)); // coef for x-
                    newVarCount += 2;
                    break;

                case SignRestriction.Int:
                case SignRestriction.Bin:
                    // Continuous relaxation here; integrality handled by B&B elsewhere. Assume x >= 0 by default for LP relaxation.
                    map.Add((j, new[] { newVarCount }, new[] { 1.0 }));
                    cList.Add(dirSign * v.ObjectiveCoeff);
                    newVarCount++;
                    break;

                default:
                    throw new NotSupportedException("Unknown sign restriction");
            }
        }

        // Step 3: transform constraints to = with b >= 0; add slacks
        var rows = new List<double[]>();
        var b = new List<double>();
        var relationList = new List<Relation>();

        foreach (var ct in model.Constraints)
        {
            // Expand coeffs by mapping
            var row = new double[newVarCount];
            Array.Fill(row, 0.0);
            for (int j = 0; j < model.NumVars; j++)
            {
                var (orig, idxs, mults) = map[j];
                for (int k = 0; k < idxs.Length; k++)
                    row[idxs[k]] += ct.Coeffs[j] * mults[k];
            }

            double rhs = ct.Rhs;
            Relation rel = ct.Relation;

            // Make rhs >= 0
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
            }

            // Convert >= to <= by multiplying by -1 again (and we’ll add slacks only for <=)
            if (rel == Relation.GreaterOrEqual)
            {
                for (int k = 0; k < row.Length; k++) row[k] *= -1.0;
                rhs *= -1.0;
                rel = Relation.LessOrEqual;
            }

            rows.Add(row);
            b.Add(rhs);
            relationList.Add(rel);
        }

        // Count number of = constraints (would require artificials for Phase I)
        int eqCount = relationList.Count(r => r == Relation.Equal);
        if (eqCount > 0)
            throw new NotSupportedException("Equality constraints require Phase I (artificial variables). Add Phase I before using general '=' models.");

        // Add slacks for all =
        int m = rows.Count;
        int n = newVarCount + m;
        var A = new double[m, n];
        for (int i = 0; i < m; i++)
        {
            var row = rows[i];
            for (int j = 0; j < newVarCount; j++) A[i, j] = row[j];
            A[i, newVarCount + i] = 1.0; // slack
        }

        var c = new double[n];
        for (int j = 0; j < newVarCount; j++) c[j] = cList[j];
        // slack coefficients = 0 by default

        var basic = Enumerable.Range(newVarCount, m).ToArray();
        var nonBasic = Enumerable.Range(0, newVarCount).ToArray();

        return new Result(new CanonicalForm(A, b.ToArray(), c, 0.0, basic, nonBasic));
    }
}
