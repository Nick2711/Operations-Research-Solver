namespace Solver.Engine.Core
{
    public sealed class SensitivityPayload
    {
        public double[,] B { get; }
        public double[,] BInv { get; }
        public double[,] N { get; }
        public double[] cB { get; }
        public double[] cN { get; }
        public double[] b { get; }         // basic values (current RHS for basic rows)
        public int[] Basis { get; }
        public int[] Nonbasic { get; }
        public double[] ShadowPrices { get; }  // y = cB^T B^{-1}

        public SensitivityPayload(
            double[,] B,
            double[,] BInv,
            double[,] N,
            double[] cB,
            double[] cN,
            double[] b,
            int[] basis,
            int[] nonbasic,
            double[] shadowPrices)
        {
            this.B = B;
            this.BInv = BInv;
            this.N = N;
            this.cB = cB;
            this.cN = cN;
            this.b = b;
            this.Basis = basis;
            this.Nonbasic = nonbasic;
            this.ShadowPrices = shadowPrices;
        }
    }
}
