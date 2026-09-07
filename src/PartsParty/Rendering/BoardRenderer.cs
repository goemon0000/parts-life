using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PartsParty.Rendering;

/// <summary>基板に挿さっている1体ぶんの情報。</summary>
public sealed record BoardSlot(
    string CharacterId,
    SlotKind Kind,
    string Motion,
    int Frame,
    int Level = 1,
    double LevelProgress = 0);

public enum SlotKind { Socket, Dimm, Pcie, M2, Sata, Atx }

/// <summary>
/// 基板とキャラを1枚の絵に描く。
///
/// **ドット絵の解像度のまま描いて、表示側で整数倍に拡大する。**
/// 拡大してから描くと、線の太さが拡大率で変わってドット絵に見えなくなる。
///
/// 配置の数値は tools/mock/index.html で目視して決めたものと同じ。
/// 見た目を変えるときは、先にあの見本で確かめてからここへ移すこと。
/// </summary>
public sealed class BoardRenderer
{
    // --- 配置（単位はドット絵の1画素）---
    public const int Cell = 32;
    /// <summary>スロット同士の間。**広めに取る。**
    /// 詰めると基板というより一列のアイコンに見え、レベルの字も入らない。</summary>
    public const int Gap = 15;
    public const int PadX = 10;
    public const int BoardTop = 5;
    public const int SlotH = 8;
    public const int BoardPadBottom = 3;
    /// <summary>スロットの下に取る、レベルを書くための帯。</summary>
    public const int LabelH = 11;
    /// <summary>足がスロットに入る深さ。これが無いと「乗っているだけ」に見える。</summary>
    public const int SlotInset = 4;

    public static int BoardHeight => BoardTop + Cell + SlotH + LabelH + BoardPadBottom;
    /// <summary>レベルの帯の上端（ドット絵の座標）。</summary>
    public static int LabelBandTop => BoardTop + Cell - SlotInset + SlotH;
    /// <summary>i 番目のスロットの左端（ドット絵の座標）。文字を重ねる側もこれを使う。</summary>
    public static int SlotLeft(int index) => PadX + index * (Cell + Gap);
    public static int BoardWidth(int slotCount) =>
        PadX * 2 + slotCount * Cell + Math.Max(0, slotCount - 1) * Gap;

    // --- 色 ---
    private static readonly Color Pcb      = C(0x22, 0x40, 0x2f);
    private static readonly Color PcbLine  = C(0x2e, 0x55, 0x40);
    private static readonly Color PcbEdge  = C(0x18, 0x2d, 0x22);
    private static readonly Color Slot     = C(0x0f, 0x17, 0x20);
    private static readonly Color SlotLip  = C(0x4d, 0x5a, 0x70);
    private static readonly Color Gold     = C(0xd8, 0xb2, 0x5c);
    private static readonly Color ChipRes  = C(0x1b, 0x2b, 0x22);
    private static readonly Color ChipCap  = C(0x2a, 0x35, 0x50);
    private static readonly Color ExpTrack = C(0x1b, 0x33, 0x27);
    private static readonly Color ExpFill  = C(0x5d, 0xd8, 0xbb);

    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private readonly Atlas _atlas;
    public BoardRenderer(Atlas atlas) => _atlas = atlas;

    /// <summary>基板1枚を描く。返るのは等倍（拡大していない）の絵。</summary>
    public BitmapSource Render(IReadOnlyList<BoardSlot> slots)
    {
        int w = BoardWidth(slots.Count);
        int h = BoardHeight;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            DrawBoard(dc, w, h);

            int charY = BoardTop;
            int slotY = charY + Cell - SlotInset;

            // 奥 → キャラ → 手前 の順。これで「挿さっている」ように見える。
            for (int i = 0; i < slots.Count; i++)
                DrawSlotBack(dc, slots[i].Kind, SlotX(i), slotY, Cell, SlotH);

            for (int i = 0; i < slots.Count; i++)
                DrawCharacter(dc, slots[i], SlotX(i), charY);

            for (int i = 0; i < slots.Count; i++)
                DrawSlotFront(dc, slots[i].Kind, SlotX(i), slotY, Cell, SlotH);

            // 経験値の進み具合。数字は上に重ねる文字で出すので、ここは線だけ。
            for (int i = 0; i < slots.Count; i++)
                DrawExpBar(dc, SlotX(i), LabelBandTop + LabelH - 3, Cell, slots[i].LevelProgress);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static int SlotX(int index) => SlotLeft(index);

    private static void DrawExpBar(DrawingContext dc, int x, int y, int w, double progress)
    {
        Fill(dc, ExpTrack, x + 2, y, w - 4, 1);
        int filled = (int)Math.Round((w - 4) * Math.Clamp(progress, 0, 1));
        if (filled > 0) Fill(dc, ExpFill, x + 2, y, filled, 1);
    }

    private void DrawCharacter(DrawingContext dc, BoardSlot slot, int x, int y)
    {
        var src = _atlas.Frame(slot.CharacterId, slot.Motion, slot.Frame);
        if (src is null) return;
        var cropped = new CroppedBitmap(_atlas.Characters, src.Value);
        dc.DrawImage(cropped, new Rect(x, y, Cell, Cell));
    }

    private static void Fill(DrawingContext dc, Color c, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, y, w, h));
    }

    private static void DrawSlotBack(DrawingContext dc, SlotKind kind, int x, int y, int w, int h)
    {
        switch (kind)
        {
            case SlotKind.Socket: Fill(dc, Slot, x - 3, y, w + 6, h); break;
            case SlotKind.Pcie:   Fill(dc, Slot, x - 4, y, w + 8, h); break;
            case SlotKind.Atx:    Fill(dc, Slot, x + 1, y, w - 2, h); break;
            default:              Fill(dc, Slot, x + 2, y, w - 4, h); break;
        }
    }

    private static void DrawSlotFront(DrawingContext dc, SlotKind kind, int x, int y, int w, int h)
    {
        int bot = y + h;
        switch (kind)
        {
            case SlotKind.Socket:
                Fill(dc, SlotLip, x - 3, y, 3, h);
                Fill(dc, SlotLip, x + w, y, 3, h);
                Fill(dc, SlotLip, x - 3, y, w + 6, 1);
                for (int i = 1; i < w - 1; i += 2) Fill(dc, Gold, x + i, bot - 2, 1, 1);
                break;

            case SlotKind.Dimm:
                // 両端の白いツメ。DIMM だと分かる一番の目印
                Fill(dc, SlotLip, x, y - 5, 3, h + 5);
                Fill(dc, SlotLip, x + w - 3, y - 5, 3, h + 5);
                Fill(dc, PcbEdge, x, y - 5, 3, 2);
                Fill(dc, PcbEdge, x + w - 3, y - 5, 3, 2);
                Fill(dc, Gold, x + 4, bot - 2, w - 8, 1);
                break;

            case SlotKind.Pcie:
                Fill(dc, SlotLip, x + w + 1, y - 4, 3, h + 4);
                Fill(dc, SlotLip, x - 4, y, w + 8, 1);
                Fill(dc, Gold, x - 2, bot - 2, w + 4, 1);
                break;

            case SlotKind.M2:
                Fill(dc, SlotLip, x + w - 4, bot - 4, 4, 4);
                Fill(dc, PcbEdge, x + w - 3, bot - 3, 2, 2);
                Fill(dc, Gold, x + 3, bot - 2, w - 12, 1);
                break;

            case SlotKind.Sata:
                Fill(dc, SlotLip, x + 2, y, w - 4, 1);
                Fill(dc, SlotLip, x + 2, y, 2, h);
                Fill(dc, Gold, x + 6, bot - 2, w - 12, 1);
                break;

            case SlotKind.Atx:
                Fill(dc, SlotLip, x + 1, y, w - 2, 1);
                for (int i = 0; i < 6; i++) Fill(dc, Gold, x + 3 + i * 4, bot - 3, 2, 2);
                break;
        }
    }

    /// <summary>
    /// 基板。**部品を少し散らす**のが要点。
    /// 配線だけだと方眼紙に見え、チップ抵抗とコンデンサを置くと一気に基板に見える。
    /// 乱数は固定の種で回す（毎フレーム模様が変わるとちらつく）。
    /// </summary>
    private static void DrawBoard(DrawingContext dc, int w, int h)
    {
        Fill(dc, Pcb, 0, 0, w, h);
        Fill(dc, PcbEdge, 0, 0, w, 1);
        Fill(dc, PcbEdge, 0, h - 1, w, 1);

        var rnd = new Random(11);

        for (int i = 0; i < 18; i++)
        {
            int px = 3 + rnd.Next(Math.Max(1, w - 10));
            int py = 3 + rnd.Next(Math.Max(1, h - 8));
            int len = 5 + rnd.Next(14);
            if (rnd.NextDouble() > 0.5)
            {
                Fill(dc, PcbLine, px, py, Math.Min(len, w - 3 - px), 1);
                Fill(dc, PcbLine, Math.Min(px + len, w - 4), py, 1, Math.Min(5, h - 3 - py));
            }
            else
            {
                Fill(dc, PcbLine, px, py, 1, Math.Min(len, h - 3 - py));
                Fill(dc, PcbLine, px, Math.Min(py + len, h - 4), Math.Min(6, w - 3 - px), 1);
            }
        }

        for (int i = 0; i < 22; i++)
        {
            int px = 4 + rnd.Next(Math.Max(1, w - 10));
            int py = 3 + rnd.Next(Math.Max(1, h - 7));
            if (rnd.NextDouble() > 0.55)
            {
                Fill(dc, ChipRes, px, py, 3, 2);
                Fill(dc, Gold, px, py, 1, 2);
                Fill(dc, Gold, px + 2, py, 1, 2);
            }
            else
            {
                Fill(dc, ChipCap, px, py, 2, 3);
                Fill(dc, SlotLip, px, py, 2, 1);
            }
        }

        foreach (var (dx, dy) in new[] { (2, 2), (w - 4, 2), (2, h - 4), (w - 4, h - 4) })
        {
            Fill(dc, SlotLip, dx, dy, 2, 2);
            Fill(dc, PcbEdge, dx, dy, 1, 1);
        }
    }
}
