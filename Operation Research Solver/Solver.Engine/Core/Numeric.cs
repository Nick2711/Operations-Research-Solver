using System;
using System.Globalization;

namespace Solver.Engine.Core
{
    public static class Numeric
    {
        public const double EPS = 1e-9;
        public static string F(double v)
        {
            if (double.IsNaN(v)) return "";
            if (Math.Abs(v) < EPS) v = 0.0;
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
