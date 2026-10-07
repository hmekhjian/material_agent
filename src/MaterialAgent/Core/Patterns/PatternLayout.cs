using System;
using System.Collections.Generic;
using System.Linq;

namespace MaterialAgent.Core.Patterns
{
    public enum PatternKind
    {
        /// <summary>Running bond, half offset (stretcher bond).</summary>
        Stretcher,
        /// <summary>Running bond, third offset.</summary>
        ThirdBond,
        /// <summary>Running bond, quarter offset.</summary>
        QuarterBond,
        Stack,
        Flemish,
        English,
        /// <summary>All headers (units laid end-on), half offset.</summary>
        Header,
        Basketweave,
        Herringbone,
    }

    /// <summary>What to lay out: the pattern and the real unit and joint sizes in mm.</summary>
    public sealed class PatternSpec
    {
        public PatternKind Kind { get; set; } = PatternKind.Stretcher;
        /// <summary>Visible length of a unit (brick 215, plank 1200, tile 600).</summary>
        public double UnitLengthMm { get; set; } = 215;
        /// <summary>Visible height/width of a unit (brick 65).</summary>
        public double UnitHeightMm { get; set; } = 65;
        /// <summary>Depth of the unit; the visible length of a header (brick laid end-on). Bonds with headers only.</summary>
        public double UnitDepthMm { get; set; } = 102.5;
        public double JointMm { get; set; } = 10;
    }

    /// <summary>One unit's visible face in mm within the pattern repeat (may extend past it; render wraps).</summary>
    public struct UnitRect
    {
        public double X, Y, W, H;
        /// <summary>True when the unit's length runs vertically (herringbone, basketweave), so texture grain turns with it.</summary>
        public bool Vertical;
        /// <summary>True for headers (the unit's end face).</summary>
        public bool Header;

        public UnitRect(double x, double y, double w, double h, bool vertical = false, bool header = false)
        { X = x; Y = y; W = w; H = h; Vertical = vertical; Header = header; }
    }

    /// <summary>One exact repeat of a pattern: its size and the units in it. Y runs up, like a wall.</summary>
    public sealed class PatternLayout
    {
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double JointMm { get; set; }
        public List<UnitRect> Units { get; } = new List<UnitRect>();
        /// <summary>Size adjustments made so the pattern closes (e.g. herringbone needs length = k × width).</summary>
        public List<string> Notes { get; } = new List<string>();

        public static PatternLayout Create(PatternSpec s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!(s.UnitLengthMm > 0) || !(s.UnitHeightMm > 0)) throw new ArgumentException("Unit length and height must be positive.");
            if (s.JointMm < 0) throw new ArgumentException("Joint must not be negative.");

            double j = s.JointMm, L = s.UnitLengthMm, H = s.UnitHeightMm, D = s.UnitDepthMm > 0 ? s.UnitDepthMm : (L - j) / 2;
            var p = new PatternLayout { JointMm = j };

            switch (s.Kind)
            {
                case PatternKind.Stretcher: Running(p, L, H, j, 2); break;
                case PatternKind.ThirdBond: Running(p, L, H, j, 3); break;
                case PatternKind.QuarterBond: Running(p, L, H, j, 4); break;
                case PatternKind.Stack: Running(p, L, H, j, 1); break;
                case PatternKind.Header:
                    Running(p, D, H, j, 2);
                    for (int i = 0; i < p.Units.Count; i++) { var u = p.Units[i]; u.Header = true; p.Units[i] = u; }
                    break;
                case PatternKind.English: English(p, L, H, D, j); break;
                case PatternKind.Flemish: Flemish(p, L, H, D, j); break;
                case PatternKind.Basketweave: Basketweave(p, L, H, j); break;
                case PatternKind.Herringbone: Herringbone(p, L, H, j); break;
                default: throw new ArgumentOutOfRangeException(nameof(s));
            }
            return p;
        }

        /// <summary>Courses of equal units, each shifted by 1/<paramref name="steps"/> of a unit (1 = stack).</summary>
        static void Running(PatternLayout p, double L, double H, double j, int steps)
        {
            double a = L + j, b = H + j;
            p.WidthMm = a;
            p.HeightMm = b * steps;
            for (int r = 0; r < steps; r++)
            {
                double x0 = (r * a / steps) % a;
                p.Units.Add(new UnitRect(x0, r * b, L, H)); // overhangs wrap around when rendered
            }
        }

        /// <summary>English bond: a course of stretchers, then a course of headers. Needs length = 2 × depth + joint.</summary>
        static void English(PatternLayout p, double L, double H, double D, double j)
        {
            double a = L + j, b = H + j;
            double d = a / 2;
            if (Math.Abs(D + j - d) > 0.5) p.Notes.Add($"Header face set to {d - j:0.#} mm so two headers match one stretcher.");
            p.WidthMm = a;
            p.HeightMm = 2 * b;
            p.Units.Add(new UnitRect(0, 0, L, H));
            // Headers offset by a quarter brick (the queen closer at the wall end), so joints don't line up.
            double off = d / 2;
            for (int i = 0; i < 2; i++) p.Units.Add(new UnitRect(off + i * d, b, d - j, H, header: true));
        }

        /// <summary>Flemish bond: stretcher, header alternating; each header centred over the stretcher below.</summary>
        static void Flemish(PatternLayout p, double L, double H, double D, double j)
        {
            double a = L + j, d = D + j, b = H + j, m = a + d;
            p.WidthMm = m;
            p.HeightMm = 2 * b;
            for (int row = 0; row < 2; row++)
            {
                // Course 2 shifted so its header centre sits over the course-1 stretcher centre.
                double shift = row == 0 ? 0 : Mod(L / 2 - D / 2 - a, m);
                p.Units.Add(new UnitRect(shift, row * b, L, H));
                p.Units.Add(new UnitRect(Mod(shift + a, m), row * b, D, H, header: true));
            }
        }

        /// <summary>Square blocks of n horizontal units alternating with n vertical ones. Unit width is adjusted so n fit the length.</summary>
        static void Basketweave(PatternLayout p, double L, double H, double j)
        {
            double a = L + j;
            int n = Math.Max(2, (int)Math.Round(a / (H + j)));
            double b = a / n, w = b - j;
            if (Math.Abs(w - H) > 0.5) p.Notes.Add($"Unit width set to {w:0.#} mm so {n} units make a square block.");
            p.WidthMm = 2 * a;
            p.HeightMm = 2 * a;
            for (int bx = 0; bx < 2; bx++)
                for (int by = 0; by < 2; by++)
                {
                    bool horizontal = (bx + by) % 2 == 0;
                    for (int k = 0; k < n; k++)
                        p.Units.Add(horizontal
                            ? new UnitRect(bx * a, by * a + k * b, L, w)
                            : new UnitRect(bx * a + k * b, by * a, w, L, vertical: true));
                }
        }

        /// <summary>
        /// 90° herringbone: horizontal and vertical units in a staircase. The lattice is t1 = (b, b), t2 = (a, -a)
        /// with a = length + joint and b = width + joint; it closes into a rectangular 2a × 2a repeat when a = k·b,
        /// so the width is adjusted to the nearest such size.
        /// </summary>
        static void Herringbone(PatternLayout p, double L, double H, double j)
        {
            double a = L + j;
            int k = Math.Max(1, (int)Math.Round(a / (H + j)));
            double b = a / k, w = b - j;
            if (Math.Abs(w - H) > 0.5) p.Notes.Add($"Unit width set to {w:0.#} mm so the herringbone repeats (length = {k} × width).");
            double P = 2 * a;
            p.WidthMm = P;
            p.HeightMm = P;

            var seen = new HashSet<(long, long, bool)>();
            for (int m = -4 * k - 4; m <= 4 * k + 4; m++)
                for (int n = -4; n <= 4; n++)
                {
                    double tx = m * b + n * a, ty = m * b - n * a;
                    AddWrapped(p, seen, P, tx, ty, L, w, false);
                    AddWrapped(p, seen, P, tx + a, ty + b - a, w, L, true);
                }
        }

        static void AddWrapped(PatternLayout p, HashSet<(long, long, bool)> seen, double period, double x, double y, double w, double h, bool vertical)
        {
            // Positions a hair below the period are the same as 0 (floating-point), so key on rounded, re-wrapped values.
            long cells = (long)Math.Round(period * 100);
            long kx = ((long)Math.Round(Mod(x, period) * 100) % cells + cells) % cells;
            long ky = ((long)Math.Round(Mod(y, period) * 100) % cells + cells) % cells;
            var key = (kx, ky, vertical);
            double wx = Mod(x, period), wy = Mod(y, period);
            if (period - wx < 1e-6) wx = 0;
            if (period - wy < 1e-6) wy = 0;
            if (seen.Add(key)) p.Units.Add(new UnitRect(wx, wy, w, h, vertical));
        }

        static double Mod(double v, double m) => ((v % m) + m) % m;

        /// <summary>Total visible unit area / repeat area, for sanity checks (1 - joint share).</summary>
        public double FaceCoverage() => Units.Sum(u => u.W * u.H) / (WidthMm * HeightMm);
    }
}
