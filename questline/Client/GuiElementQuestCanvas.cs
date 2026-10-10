using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

using questline.Model;

namespace questline.Client;

/// <summary>
/// A pannable, zoomable chapter tree. Edges, arrows and node frames live in two meshes built in
/// canvas units, so panning and zooming only change the model-view matrix; item icons, labels
/// and chapter art are drawn per item, for what is in view only.
///
/// In edit mode, dragging a quest moves it, right-dragging from one quest to another reports
/// a link, and right-clicking empty canvas reports where.
/// </summary>
public class GuiElementQuestCanvas : GuiElement
{
    /// <summary>Canvas units per grid cell, before zoom and GUI scale.</summary>
    public const float CellSize = 56;
    public const float NodeSize = 40;
    const float EdgeWidth = 3;
    const float MinZoom = 0.2f, MaxZoom = 3f;
    const float LabelMinZoom = 0.6f;
    const int LabelsPerFrame = 8;
    const int AtlasCell = 64;
    /// <summary>The dialog's own texture is drawn at 50; the background and art go just above it, then edges and frames.</summary>
    const float ArtZ = 51;
    /// <summary>Quests snap to half a cell when dropped.</summary>
    const float Snap = 0.5f;

    // Fill and border per QuestState, in enum order.
    static readonly double[][] FrameFill =
    [
        [0.13, 0.13, 0.13, 0.95],
        [0.22, 0.20, 0.16, 0.95],
        [0.16, 0.20, 0.28, 0.95],
        [0.30, 0.24, 0.08, 0.95],
        [0.14, 0.26, 0.14, 0.95],
    ];
    static readonly double[][] FrameStroke =
    [
        [0.35, 0.35, 0.35, 1],
        [0.85, 0.78, 0.60, 1],
        [0.45, 0.65, 0.95, 1],
        [1.00, 0.80, 0.20, 1],
        [0.40, 0.85, 0.40, 1],
    ];
    static readonly int Shapes = Enum.GetValues<NodeShape>().Length;
    static readonly int LitEdge = ColorUtil.ColorFromRgba(220, 190, 110, 255);
    static readonly int DimEdge = ColorUtil.ColorFromRgba(90, 90, 90, 255);
    static readonly int LinkEdge = ColorUtil.ColorFromRgba(120, 200, 255, 255);
    static readonly Vec4f White = new(1, 1, 1, 1);

    readonly List<QuestCanvasNode> nodes;
    readonly List<CanvasArt> art;
    readonly AssetLocation background;
    readonly Matrixf mvMat = new();
    readonly Dictionary<int, LoadedTexture> labels = new();
    readonly CairoFont labelFont = CairoFont.WhiteDetailText();

    MeshRef edgeMesh, frameMesh, linkMesh;
    LoadedTexture frameAtlas, backgroundTexture;
    readonly LoadedTexture[] rings = new LoadedTexture[Shapes];
    bool meshesDirty = true;
    CanvasView? initialView;

    // Canvas units at the top left corner of the element.
    float panX, panY;
    float zoom = 1;
    bool panning;
    int prevMouseX, prevMouseY, downX, downY;
    // Edit mode: the quest being dragged, and the quest a link is being drawn from.
    int moving = -1, linkFrom = -1;
    bool rightDown;
    int mouseX, mouseY;

    float tourSeconds, tourElapsed;
    Action<CanvasFrameStats> onTourDone;

    public CanvasFrameStats Stats { get; } = new();
    public float Zoom => zoom;
    public int VisibleNodes { get; private set; }
    public bool Touring => onTourDone != null;
    public IReadOnlyList<QuestCanvasNode> Nodes => nodes;
    /// <summary>Images and labels with a texture, and arrows: what of the chapter art can be drawn.</summary>
    public int ArtReady => art.Count(a => a.Type == "arrow" || a.Texture is { TextureId: > 0 });
    public bool HasBackground => backgroundTexture is { TextureId: > 0 };

    /// <summary>The node drawn with a selection ring, or -1.</summary>
    public int SelectedIndex = -1;
    /// <summary>Called with a node's index when it is clicked rather than dragged.</summary>
    public Action<int> OnNodeClicked;

    /// <summary>Lets the editing gestures through.</summary>
    public bool EditMode;
    /// <summary>Edit mode: a quest was dragged and dropped at a new grid position, already snapped.</summary>
    public Action<int, float, float> OnNodeMoved;
    /// <summary>Edit mode: a link was drawn from the first quest (the prerequisite) to the second.</summary>
    public Action<int, int> OnLinkDrawn;
    /// <summary>Edit mode: empty canvas was right-clicked at this grid position, snapped.</summary>
    public Action<float, float> OnEmptyRightClicked;

    /// <summary>Where the view is. Set before composing to restore one; otherwise the tree is fitted to the canvas.</summary>
    public CanvasView View
    {
        get => new(panX, panY, zoom);
        set => initialView = value;
    }

    float ViewScale => zoom * RuntimeEnv.GUIScale;

    public GuiElementQuestCanvas(ICoreClientAPI capi, ElementBounds bounds, List<QuestCanvasNode> nodes, List<CanvasArt> art = null, AssetLocation background = null)
        : base(capi, bounds)
    {
        this.nodes = nodes;
        this.art = art ?? [];
        this.background = background;
    }

    public override void ComposeElements(Context ctxStatic, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();

        ctxStatic.SetSourceRGBA(0, 0, 0, 0.35);
        ctxStatic.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight);
        ctxStatic.Fill();
        // Framed like the book's other panels.
        EmbossRoundRectangleElement(ctxStatic, Bounds, true, 2, 1);

        ComposeAtlases();
        LoadArt();

        if (initialView is { } view)
        {
            (panX, panY, zoom) = (view.PanX, view.PanY, view.Zoom);
        }
        else
        {
            FitView();
        }
    }

    /// <summary>The canvas-unit rectangle holding every quest and piece of art.</summary>
    (float MinX, float MinY, float MaxX, float MaxY) Extent()
    {
        // Art that couldn't load is left out, so a missing picture doesn't stretch the view.
        var shown = art.Where(a => a.Type == "arrow" || a.Texture is { TextureId: > 0 }).ToList();
        var xs = nodes.Select(n => n.X).Concat(shown.SelectMany(a => a.Type == "arrow" ? new[] { a.X, a.ToX } : new[] { a.X - a.Width / 2, a.X + a.Width / 2 })).ToList();
        var ys = nodes.Select(n => n.Y).Concat(shown.SelectMany(a => a.Type == "arrow" ? new[] { a.Y, a.ToY } : new[] { a.Y - a.Height / 2, a.Y + a.Height / 2 })).ToList();
        if (xs.Count == 0) return (0, 0, 0, 0);
        return (xs.Min() * CellSize, ys.Min() * CellSize, xs.Max() * CellSize, ys.Max() * CellSize);
    }

    /// <summary>Centres the tree, zoomed out just enough to show all of it, but never past half size or above full size.</summary>
    void FitView()
    {
        var (minX, minY, maxX, maxY) = Extent();
        float width = maxX - minX + CellSize * 2, height = maxY - minY + CellSize * 2;
        float gs = RuntimeEnv.GUIScale;
        zoom = GameMath.Clamp(Math.Min((float)Bounds.InnerWidth / (width * gs), (float)Bounds.InnerHeight / (height * gs)), 0.5f, 1f);
        float s = ViewScale;
        panX = (minX + maxX) / 2 - (float)Bounds.InnerWidth / s / 2;
        panY = (minY + maxY) / 2 - (float)Bounds.InnerHeight / s / 2;
    }

    #region Textures

    void ComposeAtlases()
    {
        int states = FrameFill.Length;
        using var atlas = new ImageSurface(Format.Argb32, AtlasCell * states, AtlasCell * Shapes);
        using var ctx = new Context(atlas);
        for (int shape = 0; shape < Shapes; shape++)
        {
            for (int i = 0; i < states; i++)
            {
                ShapePath(ctx, (NodeShape)shape, i * AtlasCell + AtlasCell / 2.0, shape * AtlasCell + AtlasCell / 2.0, AtlasCell - 8);
                var f = FrameFill[i];
                ctx.SetSourceRGBA(f[0], f[1], f[2], f[3]);
                ctx.FillPreserve();
                var s = FrameStroke[i];
                ctx.SetSourceRGBA(s[0], s[1], s[2], s[3]);
                ctx.LineWidth = 4;
                ctx.Stroke();
            }
        }
        frameAtlas ??= new LoadedTexture(api);
        api.Gui.LoadOrUpdateCairoTexture(atlas, true, ref frameAtlas);

        for (int shape = 0; shape < Shapes; shape++)
        {
            using var ring = new ImageSurface(Format.Argb32, AtlasCell, AtlasCell);
            using var ringCtx = new Context(ring);
            ShapePath(ringCtx, (NodeShape)shape, AtlasCell / 2.0, AtlasCell / 2.0, AtlasCell - 6);
            ringCtx.SetSourceRGBA(1, 1, 1, 0.9);
            ringCtx.LineWidth = 3;
            ringCtx.Stroke();
            rings[shape] ??= new LoadedTexture(api);
            api.Gui.LoadOrUpdateCairoTexture(ring, true, ref rings[shape]);
        }
    }

    /// <summary>A frame outline of <paramref name="size"/> centred on (cx, cy).</summary>
    static void ShapePath(Context ctx, NodeShape shape, double cx, double cy, double size)
    {
        double r = size / 2;
        ctx.NewPath();
        switch (shape)
        {
            case NodeShape.Circle:
                ctx.Arc(cx, cy, r, 0, 2 * Math.PI);
                break;

            case NodeShape.Hexagon:
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 3 * i + Math.PI / 6;
                    if (i == 0) ctx.MoveTo(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                    else ctx.LineTo(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                }
                ctx.ClosePath();
                break;

            case NodeShape.Gear:
                const int teeth = 10;
                for (int i = 0; i < teeth * 4; i++)
                {
                    // Each tooth: up the flank, across the top, down the flank, across the root.
                    double a = 2 * Math.PI * i / (teeth * 4);
                    double rr = (i % 4 is 1 or 2) ? r : r * 0.8;
                    if (i == 0) ctx.MoveTo(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a));
                    else ctx.LineTo(cx + rr * Math.Cos(a), cy + rr * Math.Sin(a));
                }
                ctx.ClosePath();
                break;

            default:
                RoundRectangle(ctx, cx - r, cy - r, size, size, size / 7);
                break;
        }
    }

    void LoadArt()
    {
        if (background != null)
        {
            backgroundTexture ??= new LoadedTexture(api);
            api.Render.GetOrLoadTexture(background, ref backgroundTexture);
        }

        foreach (var a in art)
        {
            if (a.Type == "image" && a.Src != null)
            {
                a.Texture ??= new LoadedTexture(api);
                api.Render.GetOrLoadTexture(a.Src, ref a.Texture);
            }
            else if (a.Type == "text" && a.Text != null && a.Texture == null)
            {
                var font = CairoFont.WhiteSmallText().WithFontSize((float)(GuiStyle.SmallFontSize * a.Scale)).WithColor(a.TextColor);
                a.Texture = api.Gui.TextTexture.GenTextTexture(a.Text, font);
            }
        }
    }

    #endregion

    #region Meshes

    /// <summary>Call after any node's state or position changes.</summary>
    public void MarkDirty() => meshesDirty = true;

    void RebuildMeshes()
    {
        int segments = nodes.Sum(n => n.Prereqs.Length) + art.Count(a => a.Type == "arrow") * 3;
        var edges = new MeshData(segments * 4, segments * 6, false, true, true, false);
        foreach (var a in art.Where(a => a.Type == "arrow")) AddArrow(edges, a);
        foreach (var n in nodes)
        {
            foreach (int p in n.Prereqs)
            {
                var from = nodes[p];
                int color = from.State >= QuestState.Claimable ? LitEdge : DimEdge;
                AddSegment(edges, from.X * CellSize, from.Y * CellSize, n.X * CellSize, n.Y * CellSize, EdgeWidth, color);
            }
        }

        int states = FrameFill.Length;
        var frames = new MeshData(nodes.Count * 4, nodes.Count * 6, false, true, true, false);
        foreach (var n in nodes)
        {
            float half = NodeSize * n.Size / 2;
            float cx = n.X * CellSize, cy = n.Y * CellSize;
            float u0 = (float)n.State / states, u1 = ((float)n.State + 1) / states;
            float v0 = (float)n.Shape / Shapes, v1 = ((float)n.Shape + 1) / Shapes;
            int b = frames.VerticesCount;
            frames.AddVertex(cx - half, cy - half, 0, u0, v0, ColorUtil.WhiteArgb);
            frames.AddVertex(cx + half, cy - half, 0, u1, v0, ColorUtil.WhiteArgb);
            frames.AddVertex(cx + half, cy + half, 0, u1, v1, ColorUtil.WhiteArgb);
            frames.AddVertex(cx - half, cy + half, 0, u0, v1, ColorUtil.WhiteArgb);
            frames.AddIndices(b, b + 1, b + 2, b, b + 2, b + 3);
        }

        DeleteMeshes();
        if (edges.VerticesCount > 0) edgeMesh = api.Render.UploadMesh(edges);
        if (nodes.Count > 0) frameMesh = api.Render.UploadMesh(frames);
    }

    static void AddArrow(MeshData mesh, CanvasArt a)
    {
        float x1 = a.X * CellSize, y1 = a.Y * CellSize, x2 = a.ToX * CellSize, y2 = a.ToY * CellSize;
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float ux = dx / len, uy = dy / len;
        float head = Math.Max(10, a.LineWidth * 4);
        AddSegment(mesh, x1, y1, x2, y2, a.LineWidth, a.Color);
        // The head: two strokes back from the tip.
        AddSegment(mesh, x2, y2, x2 - head * (ux - uy * 0.6f), y2 - head * (uy + ux * 0.6f), a.LineWidth, a.Color);
        AddSegment(mesh, x2, y2, x2 - head * (ux + uy * 0.6f), y2 - head * (uy - ux * 0.6f), a.LineWidth, a.Color);
    }

    static void AddSegment(MeshData mesh, float x1, float y1, float x2, float y2, float width, int color)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;
        float nx = -dy / len * width / 2, ny = dx / len * width / 2;

        int b = mesh.VerticesCount;
        mesh.AddVertex(x1 + nx, y1 + ny, 0, 0, 0, color);
        mesh.AddVertex(x2 + nx, y2 + ny, 0, 0, 0, color);
        mesh.AddVertex(x2 - nx, y2 - ny, 0, 0, 0, color);
        mesh.AddVertex(x1 - nx, y1 - ny, 0, 0, 0, color);
        mesh.AddIndices(b, b + 1, b + 2, b, b + 2, b + 3);
    }

    #endregion

    #region Rendering

    public override void RenderInteractiveElements(float deltaTime)
    {
        long start = Stopwatch.GetTimestamp();

        if (Touring) AdvanceTour(deltaTime);
        if (meshesDirty)
        {
            RebuildMeshes();
            meshesDirty = false;
        }

        float s = ViewScale;
        double ox = Bounds.renderX - panX * s, oy = Bounds.renderY - panY * s;

        api.Render.PushScissor(Bounds);

        RenderBackground();
        RenderArt(s, ox, oy);

        var prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("rgbaIn", White);
        prog.Uniform("extraGlow", 0);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        mvMat.Set(api.Render.CurrentModelviewMatrix).Translate(ox, oy, ArtZ + 5).Scale(s, s, 1);
        prog.UniformMatrix("modelViewMatrix", mvMat.Values);

        if (edgeMesh != null || linkFrom >= 0)
        {
            prog.Uniform("applyColor", 1);
            prog.Uniform("noTexture", 1f);
            if (edgeMesh != null) api.Render.RenderMesh(edgeMesh);
            RenderLinkInProgress(prog, s);
        }

        if (frameMesh != null)
        {
            prog.Uniform("applyColor", 0);
            prog.Uniform("noTexture", 0f);
            prog.BindTexture2D("tex2d", frameAtlas.TextureId, 0);
            api.Render.GlToggleBlend(true, EnumBlendMode.PremultipliedAlpha);
            api.Render.RenderMesh(frameMesh);
            api.Render.GlToggleBlend(true);
        }

        RenderVisibleNodes(s, ox, oy);

        api.Render.PopScissor();

        Stats.Record(deltaTime * 1000, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>The chapter background fills the canvas, cropped to keep its shape; it doesn't pan.</summary>
    void RenderBackground()
    {
        if (backgroundTexture is not { TextureId: > 0, Width: > 0 }) return;
        double w = Bounds.InnerWidth, h = Bounds.InnerHeight;
        double scale = Math.Max(w / backgroundTexture.Width, h / backgroundTexture.Height);
        double bw = backgroundTexture.Width * scale, bh = backgroundTexture.Height * scale;
        api.Render.Render2DTexture(backgroundTexture.TextureId,
            (float)(Bounds.renderX + (w - bw) / 2), (float)(Bounds.renderY + (h - bh) / 2), (float)bw, (float)bh, ArtZ);
    }

    void RenderArt(float s, double ox, double oy)
    {
        foreach (var a in art)
        {
            if (a.Texture is not { TextureId: > 0 }) continue;
            double cx = ox + a.X * CellSize * s, cy = oy + a.Y * CellSize * s;
            if (a.Type == "image")
            {
                double w = a.Width * CellSize * s, h = a.Height * CellSize * s;
                api.Render.Render2DTexture(a.Texture.TextureId, (float)(cx - w / 2), (float)(cy - h / 2), (float)w, (float)h, ArtZ + 1);
            }
            else if (a.Type == "text")
            {
                double w = a.Texture.Width * zoom, h = a.Texture.Height * zoom;
                api.Render.Render2DTexturePremultipliedAlpha(a.Texture.TextureId, (float)(cx - w / 2), (float)(cy - h / 2), (float)w, (float)h, ArtZ + 2);
            }
        }
    }

    void RenderLinkInProgress(IShaderProgram prog, float s)
    {
        if (linkFrom < 0) return;
        var from = nodes[linkFrom];
        float tx = panX + (float)(mouseX - Bounds.absX) / s, ty = panY + (float)(mouseY - Bounds.absY) / s;
        var mesh = new MeshData(4, 6, false, true, true, false);
        AddSegment(mesh, from.X * CellSize, from.Y * CellSize, tx, ty, EdgeWidth, LinkEdge);
        if (mesh.VerticesCount == 0) return;
        if (linkMesh == null) linkMesh = api.Render.UploadMesh(mesh);
        else api.Render.UpdateMesh(linkMesh, mesh);
        api.Render.RenderMesh(linkMesh);
    }

    void RenderVisibleNodes(float s, double ox, double oy)
    {
        float x2 = panX + (float)Bounds.InnerWidth / s, y2 = panY + (float)Bounds.InnerHeight / s;
        bool showLabels = zoom >= LabelMinZoom;
        int labelBudget = LabelsPerFrame;
        int visible = 0;

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            float size = NodeSize * n.Size;
            float cx = n.X * CellSize, cy = n.Y * CellSize;
            if (cx < panX - size || cx > x2 + size || cy < panY - size || cy > y2 + size) continue;
            visible++;

            double sx = ox + cx * s, sy = oy + cy * s;
            if (i == SelectedIndex)
            {
                float ringSize = size * 1.3f * s;
                api.Render.Render2DTexturePremultipliedAlpha(rings[(int)n.Shape].TextureId, (float)(sx - ringSize / 2), (float)(sy - ringSize / 2), ringSize, ringSize, 150);
            }
            if (n.Icon?.Itemstack != null)
            {
                api.Render.RenderItemstackToGui(n.Icon, sx, sy, 200, size * 0.55f * s, ColorUtil.WhiteArgb, true, false, false);
            }

            if (showLabels && GetLabel(i, ref labelBudget) is { } tex)
            {
                api.Render.Render2DTexturePremultipliedAlpha(tex.TextureId,
                    (float)(sx - tex.Width / 2.0), (float)(sy + size / 2 * s + 2), tex.Width, tex.Height, 250);
            }
        }

        VisibleNodes = visible;
    }

    // Labels are generated lazily, a few per frame, so a first zoom-in doesn't hitch. They wrap
    // at a little under two cells, so neighbours' labels don't run into each other.
    LoadedTexture GetLabel(int index, ref int budget)
    {
        if (labels.TryGetValue(index, out var tex)) return tex;
        if (budget <= 0) return null;
        budget--;
        // Measured here: the game's own max-width overload rounds the width down and then wraps every word.
        string title = nodes[index].Title;
        int max = (int)(CellSize * 1.8f * RuntimeEnv.GUIScale);
        int width = (int)Math.Ceiling(Math.Min(max, labelFont.GetTextExtents(title).Width)) + 4;
        double height = new TextDrawUtil().GetMultilineTextHeight(labelFont, title, width);
        tex = api.Gui.TextTexture.GenTextTexture(title, labelFont, width, (int)height + 2, null, EnumTextOrientation.Center);
        labels[index] = tex;
        return tex;
    }

    #endregion

    #region Mouse

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseDownOnElement(api, args);
        prevMouseX = downX = mouseX = args.X;
        prevMouseY = downY = mouseY = args.Y;

        if (args.Button == EnumMouseButton.Left)
        {
            moving = EditMode ? NodeAt(args.X, args.Y) : -1;
            panning = moving < 0;
            args.Handled = true;
        }
        else if (args.Button == EnumMouseButton.Right && EditMode)
        {
            rightDown = true;
            linkFrom = NodeAt(args.X, args.Y);
            args.Handled = true;
        }
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        base.OnMouseUp(api, args);
        bool click = Math.Abs(args.X - downX) + Math.Abs(args.Y - downY) <= 4;

        if (rightDown && args.Button == EnumMouseButton.Right)
        {
            rightDown = false;
            int from = linkFrom;
            linkFrom = -1;
            int to = NodeAt(args.X, args.Y);
            if (from >= 0 && to >= 0 && to != from) OnLinkDrawn?.Invoke(from, to);
            else if (from < 0 && click)
            {
                var (gx, gy) = GridAt(args.X, args.Y);
                OnEmptyRightClicked?.Invoke(gx, gy);
            }
            return;
        }

        if (moving >= 0)
        {
            int index = moving;
            moving = -1;
            var n = nodes[index];
            if (click)
            {
                OnNodeClicked?.Invoke(index);
                return;
            }
            n.X = SnapTo(n.X);
            n.Y = SnapTo(n.Y);
            MarkDirty();
            OnNodeMoved?.Invoke(index, n.X, n.Y);
            return;
        }

        if (!panning) return;
        panning = false;

        // A press that barely moved is a click, not a drag.
        if (!click) return;
        int hit = NodeAt(args.X, args.Y);
        if (hit >= 0) OnNodeClicked?.Invoke(hit);
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        mouseX = args.X;
        mouseY = args.Y;
        float s = ViewScale;

        if (moving >= 0)
        {
            var n = nodes[moving];
            n.X += (args.X - prevMouseX) / s / CellSize;
            n.Y += (args.Y - prevMouseY) / s / CellSize;
            MarkDirty();
        }
        else if (panning)
        {
            panX -= (args.X - prevMouseX) / s;
            panY -= (args.Y - prevMouseY) / s;
        }
        prevMouseX = args.X;
        prevMouseY = args.Y;
    }

    static float SnapTo(float v) => MathF.Round(v / Snap) * Snap;

    /// <summary>The grid position under a screen position, snapped.</summary>
    public (float X, float Y) GridAt(double screenX, double screenY)
    {
        float s = ViewScale;
        return (SnapTo((panX + (float)(screenX - Bounds.absX) / s) / CellSize), SnapTo((panY + (float)(screenY - Bounds.absY) / s) / CellSize));
    }

    /// <summary>Where a grid position is on screen, for tests that click it.</summary>
    public (int X, int Y) ScreenPosOfGrid(float gridX, float gridY)
    {
        float s = ViewScale;
        return ((int)(Bounds.absX + (gridX * CellSize - panX) * s), (int)(Bounds.absY + (gridY * CellSize - panY) * s));
    }

    /// <summary>Where a node's centre is on screen, for tests that click it.</summary>
    public (int X, int Y) ScreenPosOf(int index) => ScreenPosOfGrid(nodes[index].X, nodes[index].Y);

    /// <summary>The node under a screen position, or -1.</summary>
    public int NodeAt(double screenX, double screenY)
    {
        float s = ViewScale;
        float cx = panX + (float)(screenX - Bounds.absX) / s, cy = panY + (float)(screenY - Bounds.absY) / s;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            float half = NodeSize * nodes[i].Size / 2;
            if (Math.Abs(nodes[i].X * CellSize - cx) <= half && Math.Abs(nodes[i].Y * CellSize - cy) <= half) return i;
        }
        return -1;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;

        ZoomAt(api.Input.MouseX - Bounds.absX, api.Input.MouseY - Bounds.absY, args.delta > 0 ? 1.15f : 1 / 1.15f);
        args.SetHandled(true);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the canvas point under (viewX, viewY) in place.</summary>
    public void ZoomAt(double viewX, double viewY, float factor)
    {
        float s = ViewScale;
        float cx = panX + (float)viewX / s, cy = panY + (float)viewY / s;

        zoom = GameMath.Clamp(zoom * factor, MinZoom, MaxZoom);

        s = ViewScale;
        panX = cx - (float)viewX / s;
        panY = cy - (float)viewY / s;
    }

    #endregion

    #region Benchmark tour

    /// <summary>
    /// Sweeps the view across the whole tree, zooming in and out, for <paramref name="seconds"/>,
    /// then reports the frame times seen along the way. Lets a test check smoothness without a mouse.
    /// </summary>
    public void StartTour(float seconds, Action<CanvasFrameStats> done)
    {
        tourSeconds = seconds;
        tourElapsed = 0;
        onTourDone = done;
        Stats.Reset();
    }

    void AdvanceTour(float dt)
    {
        tourElapsed += dt;
        float t = tourElapsed / tourSeconds;
        if (t >= 1)
        {
            var done = onTourDone;
            onTourDone = null;
            done(Stats);
            return;
        }

        // Two passes left to right, a slow vertical weave, zooming between fully out and close in.
        var (minX, minY, maxX, maxY) = Extent();
        zoom = MinZoom + (MaxZoom * 0.75f - MinZoom) * (0.5f - 0.5f * MathF.Cos(t * MathF.PI * 6));
        float s = ViewScale;
        float viewW = (float)Bounds.InnerWidth / s, viewH = (float)Bounds.InnerHeight / s;
        float fx = 0.5f - 0.5f * MathF.Cos(t * MathF.PI * 4);
        float fy = 0.5f - 0.5f * MathF.Cos(t * MathF.PI * 2.5f);
        panX = minX + fx * (maxX - minX) - viewW / 2;
        panY = minY + fy * (maxY - minY) - viewH / 2;
    }

    #endregion

    void DeleteMeshes()
    {
        if (edgeMesh != null) api.Render.DeleteMesh(edgeMesh);
        if (frameMesh != null) api.Render.DeleteMesh(frameMesh);
        edgeMesh = frameMesh = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        DeleteMeshes();
        if (linkMesh != null) api.Render.DeleteMesh(linkMesh);
        frameAtlas?.Dispose();
        foreach (var ring in rings) ring?.Dispose();
        foreach (var tex in labels.Values) tex.Dispose();
        labels.Clear();
        // Images are the render API's cached textures and stay with it; text art is ours.
        foreach (var a in art.Where(a => a.Type == "text")) a.Texture?.Dispose();
    }
}

public record struct CanvasView(float PanX, float PanY, float Zoom);
