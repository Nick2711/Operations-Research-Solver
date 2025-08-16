namespace Solver.Engine.Core;

/// Canonical tableau form for Primal Simplex: Ax = b with x = 0 (after transforms),
/// plus slack vars; objective in standard max form.
public sealed class CanonicalForm
{
    public double[,] A { get; }      // m x n
    public double[] b { get; }
    public double[] c { get; }       // length n
    public double z0 { get; }        // constant term (usually 0 after transforms)
    public int[] BasicIdx { get; }   // indices of basic vars (slacks initially)
    public int[] NonBasicIdx { get; }

    public CanonicalForm(double[,] A, double[] b, double[] c, double z0, int[] basic, int[] nonBasic)
    {
        this.A = A; this.b = b; this.c = c; this.z0 = z0; BasicIdx = basic; NonBasicIdx = nonBasic;
    }
}


