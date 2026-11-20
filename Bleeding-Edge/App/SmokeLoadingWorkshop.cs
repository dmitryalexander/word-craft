using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

sealed class SmokeLoadingWorkshop
{
    internal static readonly string[] Names = {
        "", "Circular wind", "Reverse circle", "Wide orbit", "Breathing circle", "Rocking current",
        "Twin swirls", "Drifting circle", "Figure eight", "Two wind jets"
    };
    readonly WordViewEffects effects;
    readonly Action redraw;
    readonly Func<Size> viewport;
    readonly Stopwatch clock = new Stopwatch();
    readonly DispatcherTimer timer;
    internal int Active { get; private set; }
    internal string Status { get { return Active == 0 ? null : Active + " · " + Names[Active]; } }

    internal SmokeLoadingWorkshop(WordViewEffects effects, Func<Size> viewport, Action redraw)
    {
        this.effects = effects;
        this.viewport = viewport;
        this.redraw = redraw;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (sender, args) => Advance();
    }

    internal void Toggle(int option)
    {
        if (option < 1 || option > 9)
            return;
        Active = Active == option ? 0 : option;
        clock.Restart();
        timer.IsEnabled = Active != 0;
        redraw();
    }

    internal void Stop()
    {
        Active = 0;
        timer.Stop();
        clock.Stop();
        redraw();
    }

    internal void SetVisible(bool visible)
    {
        timer.IsEnabled = visible && Active != 0;
    }

    void Advance()
    {
        Size size = viewport();
        if (Active == 0 || size.Width < 16 || size.Height < 16)
            return;
        effects.ApplyWind(Forces(Active, clock.Elapsed.TotalSeconds, size.Width / size.Height), size.Width / size.Height);
    }

    internal static SmokeWind[] Forces(int option, double time, double aspect)
    {
        var forces = new List<SmokeWind>(12);
        double scale = Math.Min(1, aspect);
        double strength = 1.85 * (1 - Math.Exp(-time * 4));
        switch (option)
        {
            case 1:
                Orbit(forces, .5, .5, .29 * scale, .29 * scale, aspect, strength, 1);
                break;
            case 2:
                Orbit(forces, .5, .5, .29 * scale, .29 * scale, aspect, strength, -1);
                break;
            case 3:
                Orbit(forces, .5, .5, Math.Min(.42 * aspect, .49 * scale), .24 * scale, aspect, strength, 1);
                break;
            case 4:
                double radius = (.26 + .065 * Math.Sin(time * 1.1)) * scale;
                Orbit(forces, .5, .5, radius, radius, aspect, strength, 1);
                break;
            case 5:
                Orbit(forces, .5, .5, .29 * scale, .29 * scale, aspect, strength * Math.Sin(time * .75), 1);
                break;
            case 6:
                Orbit(forces, .5 - .18 * scale / aspect, .5, .17 * scale, .20 * scale, aspect, strength, 1);
                Orbit(forces, .5 + .18 * scale / aspect, .5, .17 * scale, .20 * scale, aspect, strength, -1);
                break;
            case 7:
                Orbit(forces, .5 + .06 * scale / aspect * Math.Sin(time * .6), .5 + .055 * scale * Math.Cos(time * .73),
                    .27 * scale, .27 * scale, aspect, strength, 1);
                break;
            case 8:
                for (int i = 0; i < 10; i++)
                {
                    double angle = i * Math.PI * 2 / 10;
                    double x = .5 + .31 * scale / aspect * Math.Sin(angle);
                    double y = .5 + .23 * scale * Math.Sin(2 * angle);
                    double direction = Math.Atan2(.46 * Math.Cos(2 * angle), .31 * Math.Cos(angle));
                    forces.Add(new SmokeWind(x, y, .13 * scale, direction, strength));
                }
                break;
            case 9:
                forces.Add(new SmokeWind(.5, .5 - .23 * scale, .18 * scale, 0, strength * 1.6));
                forces.Add(new SmokeWind(.5, .5 + .23 * scale, .18 * scale, Math.PI, strength * 1.6));
                break;
        }
        return forces.ToArray();
    }

    static void Orbit(List<SmokeWind> forces, double centerX, double centerY, double width, double height,
        double aspect, double strength, int direction)
    {
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            double x = centerX + Math.Cos(angle) * width / aspect;
            double y = centerY + Math.Sin(angle) * height;
            double tangent = Math.Atan2(height * Math.Cos(angle) * direction, -width * Math.Sin(angle) * direction);
            forces.Add(new SmokeWind(x, y, Math.Min(width, height) * .85, tangent, strength));
        }
    }
}
