namespace Solver.Engine.Core
{
    /// <summary>
    /// Human-readable names for rows/columns of A after canonicalization,
    /// and simple mappings back to original variables/constraints.
    /// </summary>
    public sealed class NameMap
    {
        public static readonly NameMap Empty = new NameMap
        {
            ColumnNames = System.Array.Empty<string>(),
            RowNames = System.Array.Empty<string>()
        };

        /// <summary>Length = NumCols. e.g., x1, x2, x3, s1, s2, a2, ...</summary>
        public string[] ColumnNames { get; init; } = System.Array.Empty<string>();

        /// <summary>Length = NumRows. e.g., c1, c2, c3, ...</summary>
        public string[] RowNames { get; init; } = System.Array.Empty<string>();

        /// <summary>
        /// For each original variable index (0..n-1), which canonical column indices it became.
        /// Useful if a variable was split (URS → x⁺, x⁻).
        /// </summary>
        public int[][] VarToColumns { get; init; } = System.Array.Empty<int[]>();

        /// <summary>
        /// For each constraint index (0..m-1), indices of its slack/surplus/artificial columns (if any).
        /// </summary>
        public int[][] RowToAddedColumns { get; init; } = System.Array.Empty<int[]>();
    }
}
