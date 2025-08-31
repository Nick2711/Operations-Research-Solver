using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Models
{
    public record TableauSnapshot(
        double[,] B,         // m x m
        double[,] N,         // m x (n-m)
        double[] cB,         // m
        double[] cN,         // n-m
        double[] b,          // m
        int[] BasicIdx,      // length m, indices in A/c
        int[] NonBasicIdx,   // length n-m
        double Z,
        bool IsMax
    );

    public record ShadowPriceResult(Dictionary<int, double> Pi);
    public record ReducedCostResult(Dictionary<int, double> Rc);

    public record RangeInterval(double Lower, double Upper);
    public record RhsRangeItem(int Constraint, double CurrentRhs, RangeInterval AllowableDelta);
    public record RhsRangeResult(List<RhsRangeItem> Items);
}
