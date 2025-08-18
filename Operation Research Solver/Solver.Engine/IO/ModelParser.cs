using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Solver.Engine.Core;

namespace Solver.Engine.IO
{
    /// <summary>
    /// Tolerant text parser for simple LPs.
    /// Format:
    ///   max 60 30 20
    ///   8 6 1 <= 48
    ///   4 2 1.5 <= 20
    ///   2 1.5 0.5 <= 8
    ///   + + +                  // sign restrictions for variables: "+", "-", "urs", "int", "bin"
    ///
    /// Notes:
    /// - Comma decimals accepted (auto-converted to '.').
    /// - Comments starting with '#' or '//' are stripped.
    /// - Relation and RHS may be split ("<= 48") or glued ("<=48").
    /// - Sign restrictions are CASE-INSENSITIVE: "+", "PLUS", "i", "INT", "b", "BIN", "URS", "free", etc.
    /// - Sign line allows commas/semicolons as delimiters.
    /// </summary>
    public static class ModelParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Convenience overload: takes raw text, normalizes it, splits to lines, and returns the parsed LpModel.
        /// </summary>
        public static LpModel Parse(string rawText)
        {
            if (rawText == null) throw new ArgumentNullException(nameof(rawText));
            var lines = NormalizeToLines(rawText);
            var result = Parse(lines);
            return result.Model;
        }

        /// <summary>
        /// Original API (kept for backward compatibility): parse from cleaned lines and return a ParseResult wrapper.
        /// </summary>
        public static ParseResult Parse(string[] lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (lines.Length < 2)
                throw new InvalidOperationException("Input must contain objective, constraints, and sign restrictions.");

            int lineIdx = 0;

            // ---- Objective ----
            var objective = lines[lineIdx++].Trim();
            var toks = objective.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 2)
                throw new InvalidOperationException("Objective line must have direction and at least one coefficient.");

            var first = toks[0].ToLowerInvariant();
            OptimizeDirection dir = first switch
            {
                "max" or "maximize" or "maximum" => OptimizeDirection.Max,
                "min" or "minimize" or "minimum" => OptimizeDirection.Min,
                _ => throw new InvalidOperationException("Objective must start with 'max' or 'min'.")
            };

            var objCoeffs = toks.Skip(1).Select(ParseNumber).ToArray();
            if (objCoeffs.Length == 0)
                throw new InvalidOperationException("Objective must contain at least one coefficient.");

            var model = new LpModel(dir);
            for (int i = 0; i < objCoeffs.Length; i++)
                model.Variables.Add(new Variable($"x{i + 1}", objCoeffs[i], SignRestriction.Urs)); // temp sign; set below

            // ---- Constraints (all lines until last) ----
            var constraints = new List<Constraint>();
            while (lineIdx < lines.Length - 1) // keep last for sign restrictions
            {
                var line = lines[lineIdx++].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var ctoks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (ctoks.Count < model.NumVars + 1)
                    throw new InvalidOperationException("Constraint line too short.");

                // Try to read relation+rhs from the last token (glued), else take the last two tokens.
                Relation rel;
                double rhs;

                var lastTok = ctoks[^1];
                if (TrySplitRelationRhs(lastTok, out rel, out rhs))
                {
                    ctoks.RemoveAt(ctoks.Count - 1);
                }
                else
                {
                    if (ctoks.Count < model.NumVars + 2)
                        throw new InvalidOperationException("Constraint line missing relation/RHS.");

                    rel = ParseRelation(ctoks[^2]);
                    rhs = ParseNumber(ctoks[^1]);
                    ctoks.RemoveRange(ctoks.Count - 2, 2);
                }

                if (ctoks.Count != model.NumVars)
                    throw new InvalidOperationException("Constraint coefficients count doesn't match number of variables.");

                var coeffs = ctoks.Select(ParseSignedNumber).ToArray();
                constraints.Add(new Constraint(coeffs, rel, rhs));
            }

            model.Constraints.AddRange(constraints);

            // ---- Sign restrictions (last line) ----
            // Accept case-insensitive tokens, allow commas/semicolons; if only one token, repeat for all vars.
            var signsLine = lines[^1].Trim();

            var signToks = SplitTokens(signsLine).ToList(); // tolerant split
            if (signToks.Count == 0)
                throw new InvalidOperationException("Last line must contain sign restrictions (e.g., '+ +' or 'i i').");

            if (signToks.Count == 1 && model.NumVars > 1)
                signToks = Enumerable.Repeat(signToks[0], model.NumVars).ToList();

            if (signToks.Count != model.NumVars)
                throw new InvalidOperationException($"Last line must contain exactly one sign token per variable (expected {model.NumVars}, got {signToks.Count}).");

            for (int i = 0; i < model.NumVars; i++)
                model.Variables[i].Sign = ParseSignToken(signToks[i]);

            return new ParseResult(model);
        }

        // ===== Normalization =====

        private static string[] NormalizeToLines(string raw)
        {
            // Remove BOM
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];

            // Unify newlines
            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // Strip comments (# and //)
            var noComments = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var s = line;
                int iHash = s.IndexOf('#');
                if (iHash >= 0) s = s[..iHash];
                int iSlashes = s.IndexOf("//", StringComparison.Ordinal);
                if (iSlashes >= 0) s = s[..iSlashes];

                s = s.Replace('\u00A0', ' '); // non-breaking space
                s = s.Trim();
                if (s.Length > 0) noComments.Add(s);
            }

            // Join for regex passes
            var text = string.Join("\n", noComments);

            // Normalize decimal comma -> dot
            text = text.Replace(',', '.');

            // Ensure spaces around <=, >=, stand-alone =
            text = Regex.Replace(text, @"<=", " <= ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @">=", " >= ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"(?<![<>!])=(?!=)", " = ", RegexOptions.CultureInvariant);

            // Collapse multiple spaces
            text = Regex.Replace(text, @"[ \t]+", " ", RegexOptions.CultureInvariant);

            // Return non-empty trimmed lines
            return text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        // ===== Helpers =====

        private static Relation ParseRelation(string s) => s switch
        {
            "<=" => Relation.LessOrEqual,
            ">=" => Relation.GreaterOrEqual,
            "=" => Relation.Equal,
            _ => throw new InvalidOperationException($"Invalid relation: {s}")
        };

        private static bool TrySplitRelationRhs(string token, out Relation rel, out double rhs)
        {
            rel = Relation.LessOrEqual; rhs = 0;
            string[] rels = new[] { "<=", ">=", "=" };
            foreach (var r in rels)
            {
                int idx = token.IndexOf(r, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    rel = ParseRelation(r);
                    var rhsStr = token[(idx + r.Length)..];
                    if (rhsStr.Length == 0) return false;
                    rhs = ParseNumber(rhsStr);
                    return true;
                }
            }
            return false;
        }

        private static double ParseSignedNumber(string s) => ParseNumber(s);

        private static double ParseNumber(string s)
        {
            var t = s.StartsWith("+") ? s[1..] : s; // allow leading '+'
            if (double.TryParse(t, NumberStyles.Float, Inv, out var val))
                return val;
            throw new InvalidOperationException($"Invalid number token: {s}");
        }

        private static IEnumerable<string> SplitTokens(string s)
            => Regex.Split(s, @"[\s,;]+")
                    .Where(t => !string.IsNullOrWhiteSpace(t));

        private static SignRestriction ParseSignToken(string tok)
        {
            if (string.IsNullOrWhiteSpace(tok))
                throw new InvalidOperationException("Empty sign token.");

            var k = tok.Trim().ToLowerInvariant();

            return k switch
            {
                "+" or "plus" or "nonneg" or "non-negative" => SignRestriction.Plus,
                "-" or "minus" => SignRestriction.Minus,
                "urs" or "free" or "unrestricted" => SignRestriction.Urs,
                "i" or "int" or "integer" => SignRestriction.Int,
                "b" or "bin" or "binary" => SignRestriction.Bin,
                _ => throw new InvalidOperationException($"Invalid sign restriction: {tok}")
            };
        }
    }
}
