using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VPNAutoConnect.Core;

namespace VPNAutoConnect;

/// 托盘图标：用 GDI+ 画的盾牌，颜色和徽记随状态变化（对应 Mac 版菜单栏的 SF Symbol）
static class TrayIcons
{
    enum Kind { Connected, Busy, Disconnected, Warning }

    static readonly Dictionary<Kind, Icon> Cache = [];

    public static Icon For(VPNState state) => Get(state switch
    {
        VPNState.Connected => Kind.Connected,
        VPNState.Connecting or VPNState.Reconnecting or VPNState.Disconnecting => Kind.Busy,
        VPNState.Disconnected => Kind.Disconnected,
        _ => Kind.Warning,
    });

    static Icon Get(Kind kind)
    {
        if (Cache.TryGetValue(kind, out var icon)) return icon;
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width * 2);
        using var bmp = Render(kind, size);
        var h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[kind] = icon;
        return icon;
    }

    /// 单位坐标 (0~1) 的盾牌轮廓
    public static GraphicsPath ShieldPath(float x, float y, float w, float h)
    {
        PointF P(float px, float py) => new(x + px * w, y + py * h);
        var path = new GraphicsPath();
        path.AddLine(P(0.5f, 0.02f), P(0.95f, 0.17f));
        path.AddLine(P(0.95f, 0.17f), P(0.95f, 0.48f));
        path.AddBezier(P(0.95f, 0.48f), P(0.95f, 0.75f), P(0.72f, 0.9f), P(0.5f, 0.98f));
        path.AddBezier(P(0.5f, 0.98f), P(0.28f, 0.9f), P(0.05f, 0.75f), P(0.05f, 0.48f));
        path.AddLine(P(0.05f, 0.48f), P(0.05f, 0.17f));
        path.CloseFigure();
        return path;
    }

    static Bitmap Render(Kind kind, int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var fill = kind switch
        {
            Kind.Connected => Color.FromArgb(0x1E, 0x9E, 0x4A),
            Kind.Busy => Color.FromArgb(0x1A, 0x8C, 0xF2),
            Kind.Disconnected => Color.FromArgb(0x80, 0x80, 0x80),
            _ => Color.FromArgb(0xE8, 0x8A, 0x00),
        };
        float m = s * 0.04f;
        using (var shield = ShieldPath(m, m, s - 2 * m, s - 2 * m))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, shield);

        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, s * 0.09f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (kind)
        {
            case Kind.Connected:
                DrawLock(g, s, white, pen);
                break;
            case Kind.Busy:
                // 两段圆弧箭头
                var r = new RectangleF(s * 0.3f, s * 0.26f, s * 0.4f, s * 0.4f);
                g.DrawArc(pen, r, 200, 130);
                g.DrawArc(pen, r, 20, 130);
                break;
            case Kind.Disconnected:
                DrawLock(g, s, white, pen);
                using (var slash = new Pen(fill, s * 0.2f))
                    g.DrawLine(slash, s * 0.2f, s * 0.2f, s * 0.8f, s * 0.8f);
                using (var slash = new Pen(Color.White, s * 0.08f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(slash, s * 0.22f, s * 0.22f, s * 0.78f, s * 0.78f);
                break;
            default:
                g.DrawLine(pen, s * 0.5f, s * 0.26f, s * 0.5f, s * 0.54f);
                g.FillEllipse(white, s * 0.44f, s * 0.64f, s * 0.12f, s * 0.12f);
                break;
        }
        return bmp;
    }

    static void DrawLock(Graphics g, int s, Brush white, Pen pen)
    {
        using var shackle = new Pen(Color.White, s * 0.07f);
        g.DrawArc(shackle, s * 0.38f, s * 0.24f, s * 0.24f, s * 0.26f, 180, 180);
        g.DrawLine(shackle, s * 0.38f, s * 0.37f, s * 0.38f, s * 0.44f);
        g.DrawLine(shackle, s * 0.62f, s * 0.37f, s * 0.62f, s * 0.44f);
        g.FillRectangle(white, s * 0.32f, s * 0.43f, s * 0.36f, s * 0.27f);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
