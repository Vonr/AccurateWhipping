using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace AccurateWhipping
{
    public class AccurateWhipping : ModSystem
    {
        public override void Load()
        {
            IL_Projectile.Colliding += static (il) =>
            {
                var c = new ILCursor(il);
                var fillWhipControlPoints = typeof(Projectile).GetMethod(nameof(Projectile.FillWhipControlPoints), BindingFlags.Public | BindingFlags.Static);

                if (!c.TryGotoNext(MoveType.After, i => i.MatchCall(fillWhipControlPoints)))
                {
                    throw new InvalidProgramException($"Couldn't find call to {fillWhipControlPoints} in Colliding");
                }

                c.EmitLdarg0();
                c.EmitLdarg1();
                c.EmitLdarg2();
                c.EmitDelegate((Projectile self, Rectangle myRect, Rectangle targetRect) =>
                {
                    var cheap = BoundingRectangle(self.WhipPointsForCollision.GetEnumerator());
                    cheap.Offset(-myRect.Width / 2, -myRect.Height / 2);
                    cheap.Inflate(myRect.Width, myRect.Height);
                    if (!cheap.Intersects(targetRect) && !cheap.Contains(targetRect))
                    {
                        self.WhipPointsForCollision.Clear();
                        return false;
                    }

                    var origin = Main.GetPlayerArmPosition(self);

                    var furthest = origin;
                    var furthestPoint = origin;

                    Projectile.GetWhipSettings(self, out var timeToFlyOut, out var _, out var _);
                    List<Vector2> points = [];
                    var ai = self.ai[0];

                    for (self.ai[0] = 0; self.ai[0] < timeToFlyOut; self.ai[0]++)
                    {
                        Projectile.FillWhipControlPoints(self, points);

                        var currFurthest = points.MaxBy(p => p.DistanceSQ(origin));
                        if (self.ai[0] == ai)
                        {
                            furthestPoint = currFurthest;
                        }
                        if (currFurthest.DistanceSQ(origin) >= furthest.DistanceSQ(origin))
                        {
                            furthest = currFurthest;
                        }

                        points.Clear();
                    }

                    self.ai[0] = ai;
                    var mid = ((furthest - origin) / furthest.DistanceSQ(origin) * furthestPoint.DistanceSQ(origin)) + origin;

                    var xo = new Vector2(myRect.Width / 2, 0);
                    var yo = new Vector2(0, myRect.Height / 2);
                    Vector2[] shifts = [Vector2.Zero, xo, yo, -xo, -yo, xo + yo, -xo + yo, xo - yo, -xo - yo];
                    Vector2 shift(Vector2 p)
                    {
                        var smid = mid - p;
                        return shifts.MaxBy(s => mid.DistanceSQ(s)) + p;
                    }

                    origin = shift(origin);

                    bool c(Vector2 a, Vector2 b)
                    {
                        return Utils.RectangleLineCollision(targetRect.TopLeft(), targetRect.BottomRight(), a, b);
                    }

                    foreach (var point in self.WhipPointsForCollision.Select(shift))
                    {
                        if (IsInsideTriangle(origin, point, mid, targetRect.Center()) || c(origin, point) || c(point, mid) || c(mid, origin))
                        {
                            self.WhipPointsForCollision.Clear();
                            return true;
                        }
                    }

                    self.WhipPointsForCollision.Clear();
                    return false;
                });

                c.EmitRet();
            };
        }

        public static Rectangle BoundingRectangle(List<Vector2>.Enumerator points)
        {
            Rectangle? rectangle = null;
            while (points.MoveNext())
            {
                var point = points.Current;
                rectangle = (rectangle ?? new Rectangle((int)(point.X + 0.5), (int)(point.Y + 0.5), 0, 0)).Including(point);
            }

            return rectangle ?? new Rectangle();
        }

        // Adapted from https://stackoverflow.com/a/9755252 under CC BY-SA 4.0
        public static bool IsInsideTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 s)
        {
            var as_x = s.X - a.X;
            var as_y = s.Y - a.Y;

            var s_ab = ((b.X - a.X) * as_y) - ((b.Y - a.Y) * as_x) > 0;

            return (((c.X - a.X) * as_y) - ((c.Y - a.Y) * as_x) > 0) != s_ab && (((c.X - b.X) * (s.Y - b.Y)) - ((c.Y - b.Y) * (s.X - b.X)) > 0) == s_ab;
        }
    }

    public class AccurateWhippingMod : Mod { }
}
