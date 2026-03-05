using System;
using System.Collections.Generic;
using System.Linq;

namespace DartGameAPI.Services;

/// <summary>
/// Fallback aggregator for NULL detection results.
/// When the DLL returns Partial_1cam (only 1 camera detected the dart),
/// uses the single camera's warped tip point to score directly.
/// When 2+ cameras have data but DLL returned score=0, intersects their rays.
/// </summary>
public static class FallbackAggregator
{
    private const double BOARD_RADIUS_MM = 170.0;
    private const double MIN_ANGLE_SIN = 0.15;   // ~8.6 degrees
    private const double MAX_CLOSEST_DIST = 6.0;  // mm
    private const double MIN_QUALITY_1CAM = 0.35;
    private const int MIN_BARREL_PIXELS_1CAM = 40;

    // Standard dartboard segment order (clockwise from top)
    private static readonly int[] SEGMENT_ORDER = { 20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5 };

    /// <summary>
    /// Attempt to rescue a NULL/partial detection result using cam_debug data.
    /// Returns true if fallback produced a score, modifying result in-place.
    /// </summary>
    public static bool TryFallback(DetectionResult result, Microsoft.Extensions.Logging.ILogger logger)
    {
        // Only apply when score is 0 (NULL result) and we have partial cam data
        if (result.Score > 0) return false;
        if (result.TriDebug?.CamDebug == null || result.TriDebug.CamDebug.Count == 0) return false;

        var cams = new List<(string camId, CamTriDebug dbg)>();
        foreach (var kv in result.TriDebug.CamDebug)
        {
            cams.Add((kv.Key, kv.Value));
        }

        // Build rays from cameras that have valid warped data
        var rays = new List<(string camId, double px, double py, double dx, double dy, double quality, CamTriDebug dbg)>();
        foreach (var (camId, dbg) in cams)
        {
            double dx = dbg.WarpedDirX;
            double dy = dbg.WarpedDirY;
            double dirLen = Math.Sqrt(dx * dx + dy * dy);
            if (dirLen < 0.5) continue; // no valid direction

            // Normalize
            dx /= dirLen;
            dy /= dirLen;

            rays.Add((camId, dbg.WarpedPointX, dbg.WarpedPointY, dx, dy, dbg.DetectionQuality, dbg));
        }

        // --- Try 2-cam ray intersection first ---
        if (rays.Count >= 2)
        {
            (double x, double y, double conf, string method)? bestPair = null;
            double bestResidual = double.MaxValue;

            for (int i = 0; i < rays.Count; i++)
            {
                for (int j = i + 1; j < rays.Count; j++)
                {
                    var r1 = rays[i];
                    var r2 = rays[j];

                    // Solve ray intersection: P1 + t*D1 = P2 + u*D2
                    double a11 = r1.dx, a12 = -r2.dx;
                    double a21 = r1.dy, a22 = -r2.dy;
                    double det = a11 * a22 - a12 * a21;

                    if (Math.Abs(det) < 1e-9) continue; // parallel

                    double angleSin = Math.Abs(det); // since dirs are normalized, |det| = |sin(theta)|
                    if (angleSin < MIN_ANGLE_SIN) continue;

                    double rhsX = r2.px - r1.px;
                    double rhsY = r2.py - r1.py;
                    double t = (rhsX * a22 - a12 * rhsY) / det;
                    double u = (a11 * rhsY - rhsX * a21) / det;

                    double q1x = r1.px + t * r1.dx, q1y = r1.py + t * r1.dy;
                    double q2x = r2.px + u * r2.dx, q2y = r2.py + u * r2.dy;
                    double midX = (q1x + q2x) / 2.0, midY = (q1y + q2y) / 2.0;
                    double closestDist = Math.Sqrt((q1x - q2x) * (q1x - q2x) + (q1y - q2y) * (q1y - q2y));

                    if (closestDist > MAX_CLOSEST_DIST) continue;

                    double radius = Math.Sqrt(midX * midX + midY * midY);
                    if (radius > BOARD_RADIUS_MM * 1.03) continue; // outside board

                    if (closestDist < bestResidual)
                    {
                        bestResidual = closestDist;
                        double conf = Math.Min(0.85,
                            0.60 + 0.15 * Math.Min(1.0, angleSin / 0.35)
                                 + 0.10 * Math.Min(1.0, 6.0 / (closestDist + 1e-6)));

                        bestPair = (midX, midY, conf, $"fallback_2cam_{r1.camId}_{r2.camId}");
                    }
                }
            }

            if (bestPair.HasValue)
            {
                var (x, y, conf, method) = bestPair.Value;
                var (seg, mult, score) = ScoreFromXY(x, y);
                if (seg > 0)
                {
                    result.Segment = seg;
                    result.Multiplier = mult;
                    result.Score = score;
                    result.Method = method;
                    result.Confidence = conf;
                    result.CoordsX = x;
                    result.CoordsY = y;
                    logger?.LogInformation("[FALLBACK] 2-cam rescue: S{Seg}x{Mult}={Score} ({Method}, conf={Conf:F2}, residual={Res:F2}mm)",
                        seg, mult, score, method, conf, bestResidual);
                    return true;
                }
            }
        }

        // --- Try 1-cam warped tip fallback ---
        var validTips = cams
            .Where(c => !c.dbg.WeakBarrelSignal
                     && c.dbg.BarrelPixelCount >= MIN_BARREL_PIXELS_1CAM
                     && c.dbg.DetectionQuality >= MIN_QUALITY_1CAM)
            .OrderByDescending(c => c.dbg.DetectionQuality)
            .ToList();

        if (validTips.Count > 0)
        {
            var best = validTips[0];
            double px = best.dbg.WarpedPointX;
            double py = best.dbg.WarpedPointY;
            double radius = Math.Sqrt(px * px + py * py);

            if (radius <= BOARD_RADIUS_MM * 1.03) // inside board
            {
                var (seg, mult, score) = ScoreFromXY(px, py);
                if (seg > 0)
                {
                    result.Segment = seg;
                    result.Multiplier = mult;
                    result.Score = score;
                    result.Method = $"fallback_1cam_{best.camId}";
                    result.Confidence = 0.55;
                    result.CoordsX = px;
                    result.CoordsY = py;
                    logger?.LogInformation("[FALLBACK] 1-cam rescue: S{Seg}x{Mult}={Score} from {CamId} (quality={Q:F2}, pixels={Px})",
                        seg, mult, score, best.camId, best.dbg.DetectionQuality, best.dbg.BarrelPixelCount);
                    return true;
                }
            }
        }

        logger?.LogInformation("[FALLBACK] No viable fallback. Cams with data: {Count}, rays: {Rays}",
            cams.Count, rays.Count);
        return false;
    }

    /// <summary>
    /// Score from board XY coordinates (mm from center).
    /// Uses polar coordinates to determine segment and ring.
    /// </summary>
    private static (int segment, int multiplier, int score) ScoreFromXY(double x, double y)
    {
        double r = Math.Sqrt(x * x + y * y);
        double angle = Math.Atan2(-x, y) * 180.0 / Math.PI; // board orientation
        if (angle < 0) angle += 360.0;

        // Determine segment from angle
        // Each segment spans 18 degrees, with segment boundaries at 9-degree offsets
        int segIndex = (int)Math.Floor((angle + 9.0) / 18.0) % 20;
        int segment = SEGMENT_ORDER[segIndex];

        // Determine multiplier from radius
        int multiplier;
        if (r <= 6.35) { segment = 25; multiplier = 2; } // double bull
        else if (r <= 16.0) { segment = 25; multiplier = 1; } // single bull
        else if (r >= 99.0 && r <= 107.0) { multiplier = 3; } // triple
        else if (r >= 162.0 && r <= 170.0) { multiplier = 2; } // double
        else if (r > 170.0) { return (0, 0, 0); } // off board
        else { multiplier = 1; } // single

        int score = segment == 25 ? (multiplier == 2 ? 50 : 25) : segment * multiplier;
        return (segment, multiplier, score);
    }
}
