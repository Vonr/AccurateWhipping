using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace AccurateWhipping
{
    public class AccurateWhipping : ModSystem
    {
        public override void Load()
        {
            On_Projectile.Damage += static (orig, self) =>
            {
                if (self.TryGetGlobalProjectile(out AccurateWhippingProjectileData data))
                {
                    data.Cache = null;
                }

                orig.Invoke(self);
            };

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
                c.EmitDelegate(static (Projectile self, Rectangle myRect, Rectangle targetRect) =>
                {
                    if (!self.TryGetGlobalProjectile(out AccurateWhippingProjectileData data))
                    {
                        self.WhipPointsForCollision.Clear();
                        Main.NewText("ERROR: failed to get whip data");
                        return false;
                    }

                    Rectangle cheap;
                    if (data.Cache != null)
                    {
                        cheap = data.Cache.Cheap;
                    }
                    else
                    {
                        cheap = BoundingRectangle(CollectionsMarshal.AsSpan(self.WhipPointsForCollision));
                        cheap.Inflate(myRect.Width, myRect.Height);
                    }

                    if (!cheap.Contains(targetRect) && !cheap.Intersects(targetRect))
                    {
                        self.WhipPointsForCollision.Clear();
                        return false;
                    }

                    var origin = Main.GetPlayerArmPosition(self);

                    Vector2 mid;
                    Vector2[] shifts;
                    Vector2[] lastPoints;

                    if (data.Cache != null)
                    {
                        mid = data.Cache.Mid;
                        shifts = data.Cache.Shifts;
                        lastPoints = data.Cache.LastPoints;
                    }
                    else
                    {
                        var furthest = origin;
                        var furthestDistance = 0f;
                        var furthestPoint = origin;
                        var furthestPointDistance = 0f;

                        Projectile.GetWhipSettings(self, out var timeToFlyOut, out var segments, out var _);
                        lastPoints = new Vector2[(int)timeToFlyOut];
                        List<Vector2> points = new(segments);
                        var ai = self.ai[0];

                        for (self.ai[0] = 0; self.ai[0] < timeToFlyOut; self.ai[0]++)
                        {
                            Projectile.FillWhipControlPoints(self, points);
                            if (points.Count == 0)
                            {
                                continue;
                            }

                            lastPoints[(int)self.ai[0]] = points[^1];

                            FurthestFrom(CollectionsMarshal.AsSpan(points), origin, out var currFurthest, out var currFurthestDistance);

                            if (self.ai[0] == ai)
                            {
                                furthestPoint = currFurthest;
                                furthestPointDistance = currFurthestDistance;
                            }

                            if (currFurthestDistance >= furthestDistance)
                            {
                                furthest = currFurthest;
                                furthestDistance = currFurthestDistance;
                            }

                            points.Clear();
                        }

                        self.ai[0] = ai;
                        mid = ((furthest - origin) / furthestDistance * furthestPointDistance) + origin;

                        var xo = new Vector2(myRect.Width * 0.5f, 0);
                        var yo = new Vector2(0, myRect.Height * 0.5f);
                        shifts = [Vector2.Zero, xo, yo, -xo, -yo, xo + yo, -xo + yo, xo - yo, -xo - yo];
                    }

                    data.Cache ??= new()
                    {
                        Cheap = cheap,
                        Mid = mid,
                        Shifts = shifts,
                        LastPoints = lastPoints,
                    };

                    for (var i = 0; i < self.WhipPointsForCollision.Count; i++)
                    {
                        var point = data.ShiftVector(self.WhipPointsForCollision[i], mid);

                        if (targetRect.Clip([origin, point, mid]))
                        {
                            self.WhipPointsForCollision.Clear();
                            return true;
                        }

                        if (i == self.WhipPointsForCollision.Count - 1 && (int)self.ai[0] > 0)
                        {
                            var prev = data.ShiftVector(lastPoints[(int)self.ai[0] - 1], mid);

                            if (targetRect.Clip([prev, point]))
                            {
                                self.WhipPointsForCollision.Clear();
                                return true;
                            }
                        }
                    }

                    self.WhipPointsForCollision.Clear();
                    return false;
                });

                c.EmitRet();
            };
        }

        public static Rectangle BoundingRectangle(ReadOnlySpan<Vector2> points)
        {
            Rectangle? rectangle = null;
            foreach (var point in points)
            {
                rectangle = (rectangle ?? new((int)(point.X + 0.5f), (int)(point.Y + 0.5f), 0, 0)).Including(point);
            }

            return rectangle ?? new();
        }

        public static void FurthestFrom(ReadOnlySpan<Vector2> list, Vector2 from, out Vector2 furthest, out float distanceSQ)
        {
            furthest = Vector2.Zero;
            distanceSQ = float.NaN;
            if (list.Length == 0)
            {
                return;
            }

            var furthestIdx = 0;
            distanceSQ = 0f;
            for (var i = 0; i < list.Length; i++)
            {
                var currDistanceSQ = from.DistanceSQ(list[i]);
                if (currDistanceSQ > distanceSQ)
                {
                    furthestIdx = i;
                    distanceSQ = currDistanceSQ;
                }
            }
            furthest = list[furthestIdx];

            return;
        }
    }

    public class AccurateWhippingProjectileData : GlobalProjectile
    {
        public override bool InstancePerEntity => true;

        public CollisionInfo Cache { get; set; }

        public override bool AppliesToEntity(Projectile entity, bool lateInstantiation)
        {
            return ProjectileID.Sets.IsAWhip[entity.type] && base.AppliesToEntity(entity, lateInstantiation);
        }

        public Vector2 ShiftVector(Vector2 vec, Vector2 from)
        {
            if (Cache.Shifts.Length == 0)
            {
                return vec;
            }

            var sfrom = from - vec;

            AccurateWhipping.FurthestFrom(Cache.Shifts, sfrom, out var furthest, out var _);
            return vec + furthest;
        }

        public class CollisionInfo
        {
            public Rectangle Cheap { get; set; }
            public Vector2 Mid { get; set; }
            public Vector2[] Shifts { get; set; }
            public Vector2[] LastPoints { get; set; }
        }
    }

    // Adapted from https://en.wikipedia.org/wiki/Cohen%E2%80%93Sutherland_algorithm
    public static class CohenSutherland
    {
        private const byte Inside = 0b0000;
        private const byte Left = 0b0001;
        private const byte Right = 0b0010;
        private const byte Bottom = 0b0100;
        private const byte Top = 0b1000;

        private static byte Outcode(float minX, float minY, float maxX, float maxY, float x, float y)
        {
            var code = Inside;

            if (x < minX)
            {
                code |= Left;
            }
            else if (x > maxX)
            {
                code |= Right;
            }
            if (y < minY)
            {
                code |= Bottom;
            }
            else if (y > maxY)
            {
                code |= Top;
            }

            return code;
        }

        private static bool Clip(float minX, float minY, float maxX, float maxY, float startX, float startY, float endX, float endY)
        {
            var startOut = Outcode(minX, minY, maxX, maxY, startX, startY);
            var endOut = Outcode(minX, minY, maxX, maxY, endX, endY);

            while (true)
            {
                if ((startOut | endOut) == Inside)
                {
                    return true;
                }

                if ((startOut & endOut) != 0)
                {
                    return false;
                }

                var x = 0f;
                var y = 0f;

                var outcodeOut = endOut > startOut ? endOut : startOut;

                if ((outcodeOut & Top) != 0)
                {
                    x = startX + ((endX - startX) * (maxY - startY) / (endY - startY));
                    y = maxY;
                }
                else if ((outcodeOut & Bottom) != 0)
                {
                    x = startX + ((endX - startX) * (minY - startY) / (endY - startY));
                    y = minY;
                }
                else if ((outcodeOut & Right) != 0)
                {
                    y = startY + ((endY - startY) * (maxX - startX) / (endX - startX));
                    x = maxX;
                }
                else if ((outcodeOut & Left) != 0)
                {
                    y = startY + ((endY - startY) * (minX - startX) / (endX - startX));
                    x = minX;
                }

                if (outcodeOut == startOut)
                {
                    startX = x;
                    startY = y;
                    startOut = Outcode(minX, minY, maxX, maxY, startX, startY);
                }
                else
                {
                    endX = x;
                    endY = y;
                    endOut = Outcode(minX, minY, maxX, maxY, endX, endY);
                }
            }
        }

        public static bool Clip(this Rectangle rect, ReadOnlySpan<Vector2> points)
        {
            if (points.Length == 0)
            {
                return false;
            }

            var minX = rect.Left;
            var minY = rect.Top;
            var maxX = rect.Right;
            var maxY = rect.Bottom;

            if (points.Length == 1)
            {
                var point = points[0];
                return Outcode(minX, minY, maxX, maxY, point.X, point.Y) == Inside;
            }

            var prevPoint = points[0];
            for (var i = 1; i < points.Length; i++)
            {
                var point = points[i];
                if (Clip(minX, minY, maxX, maxY, prevPoint.X, prevPoint.Y, point.X, point.Y))
                {
                    return true;
                }

                prevPoint = point;
            }

            return points.Length > 2 && Clip(minX, minY, maxX, maxY, prevPoint.X, prevPoint.Y, points[0].X, points[0].Y);
        }
    }
}
