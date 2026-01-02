using System;
using dc;
using dc.en;
using dc.pr;
using Math = System.Math;

namespace DeadCellsMultiplayerMod;

public class Kinghead
{
    private Hero me;
    public Kinghead(Hero _me)
    {
        me = _me;
    }
    public void kinghd(KingSkin kingSkin)
    {
        Fx fx = Game.Class.ME.curLevel.fx;


        double headX = kingSkin.get_headX();
        double headY = kingSkin.get_headY();

        double dx = kingSkin.dx;
        double dy = kingSkin.dy;
        double speed = Math.Sqrt(dx * dx + dy * dy);

        // 判断移动状态
        bool isMovingHorizontally = Math.Abs(dx) > 0.01;
        bool isMovingVertically = Math.Abs(dy) > 0.01;
        bool isMoving = isMovingHorizontally || isMovingVertically;

        double flashIntensity = 0.25;
        double flashRadius = 9.0;

        if (isMoving)
        {
            flashIntensity = Math.Min(0.3, 0.25 + speed * 0.05);
            flashRadius = Math.Min(22.0, 9.0 + speed * 1.0);
        }

        FlashLight flashLight = FlashLight.Class.create(
            me._level,
            headX,
            headY,
            2001377,
            flashRadius,
            flashIntensity,
            0.06,
            null
        );

        int numLightnings = 2 + Std.Class.random(2);

        for (int i = 0; i < numLightnings; i++)
        {
            double startX, startY, endX, endY;

            if (isMoving)
            {
                int dir = kingSkin.dir;


                double forwardDistance = 3.0 + Math.Min(7.0, speed * 1.0);

                if (Math.Abs(dx) > Math.Abs(dy) || Math.Abs(dx) > 0.1)
                {
                    startX = headX + dir * forwardDistance;
                    startY = headY;

                    if (Math.Abs(dy) > 0.1)
                    {
                        startY = headY + (dy > 0 ? forwardDistance * 0.3 : -forwardDistance * 0.3);
                    }
                }
                else if (Math.Abs(dy) > Math.Abs(dx) || Math.Abs(dy) > 0.1)
                {
                    startX = headX;
                    startY = headY + (dy > 0 ? forwardDistance : -forwardDistance);

                    if (Math.Abs(dx) > 0.1)
                    {
                        startX = headX + dir * forwardDistance * 0.3;
                    }
                }
                else
                {
                    startX = headX + dir * forwardDistance * 0.5;
                    startY = headY;
                }

                double randomRange = 2.0;
                double randomX = (dc.Math.Class.random() - 0.5) * randomRange;
                double randomY = (dc.Math.Class.random() - 0.5) * randomRange;

                double extendForward = 1.5;
                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    endX = startX + dir * extendForward + randomX;
                    endY = startY + randomY;
                }
                else if (Math.Abs(dy) > Math.Abs(dx))
                {
                    endX = startX + randomX;
                    endY = startY + (dy > 0 ? extendForward : -extendForward) + randomY;
                }
                else
                {
                    endX = startX + randomX;
                    endY = startY + randomY;
                }
            }
            else
            {
                startX = headX;
                startY = headY;


                double randomRange = 2.5;
                endX = headX + (dc.Math.Class.random() - 0.5) * randomRange;
                endY = headY + (dc.Math.Class.random() - 0.5) * randomRange;
            }

            double maxDistance = 5.0;
            double distance = Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
            if (distance > maxDistance)
            {
                double ratio = maxDistance / distance;
                endX = startX + (endX - startX) * ratio;
                endY = startY + (endY - startY) * ratio;
            }

            double startDistanceToHead = Math.Sqrt((startX - headX) * (startX - headX) + (startY - headY) * (startY - headY));
            double maxStartDistance = 15.0;
            if (startDistanceToHead > maxStartDistance)
            {
                double ratio = maxStartDistance / startDistanceToHead;
                startX = headX + (startX - headX) * ratio;
                startY = headY + (startY - headY) * ratio;
            }
            fx.heroHeadLightnings(
                null,
                startX,
                startY,
                endX,
                endY,
                2001377
            );
        }
    }
}
