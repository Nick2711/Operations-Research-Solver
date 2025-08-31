using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Solver.Engine.Core
{
    // Just the matrices/vectors sensitivity needs.
    public record SensitivityPayload(
        double[,] B,         // m x m
        double[,] N,         // m x (n-m)
        double[] cB,         // m
        double[] cN,         // n-m
        double[] b,          // m
        int[] BasicIdx,      // length m
        int[] NonBasicIdx    // length n-m
    );
}
